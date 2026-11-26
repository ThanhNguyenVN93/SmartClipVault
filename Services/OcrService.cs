using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using SmartClipVault.Data;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public sealed class OcrService : IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly string? _allowedImageDirectoryPrefix;
    private readonly Channel<ClipItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private int _disposed;

    public OcrService(DatabaseService databaseService, string? allowedImageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        _databaseService = databaseService;

        if (!string.IsNullOrWhiteSpace(allowedImageDirectory))
        {
            var fullDir = Path.GetFullPath(allowedImageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _allowedImageDirectoryPrefix = fullDir + Path.DirectorySeparatorChar;
        }

        _queue = Channel.CreateBounded<ClipItem>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(ClipItem? item)
    {
        if (item is null || Volatile.Read(ref _disposed) != 0)
            return;

        if (item.ContentType != ClipContentType.Image || !TryGetValidatedImagePath(item.ImagePath, out _))
            return;

        _queue.Writer.TryWrite(item);
    }

    private async Task ProcessQueueAsync()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            Trace.TraceWarning("OcrService: no OCR engine available for user profile languages.");
            _queue.Writer.TryComplete();
            return;
        }

        var token = _cts.Token;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (!TryGetValidatedImagePath(item.ImagePath, out var validatedPath))
                    continue;

                try
                {
                    var ocrText = await ExtractOcrTextAsync(engine, validatedPath, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(ocrText))
                        _databaseService.UpdateOcrText(item.Id, ocrText);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"OcrService: failed processing clip {item.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryGetValidatedImagePath(string? imagePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
            return false;

        try
        {
            var candidate = Path.GetFullPath(imagePath);
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            if (_allowedImageDirectoryPrefix is not null &&
                !candidate.StartsWith(_allowedImageDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string> ExtractOcrTextAsync(OcrEngine engine, string imagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(imagePath))
            return string.Empty;

        using var fileStream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var stream = fileStream.AsRandomAccessStream();

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var transform = CreateScaledTransformIfNeeded(decoder.PixelWidth, decoder.PixelHeight);

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    private static BitmapTransform CreateScaledTransformIfNeeded(uint width, uint height)
    {
        var transform = new BitmapTransform();
        var maxDim = OcrEngine.MaxImageDimension;
        if (maxDim == 0 || (width <= maxDim && height <= maxDim))
            return transform;

        var scale = Math.Min((double)maxDim / width, (double)maxDim / height);
        transform.ScaledWidth = Math.Max(1u, (uint)Math.Floor(width * scale));
        transform.ScaledHeight = Math.Max(1u, (uint)Math.Floor(height * scale));
        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        return transform;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        var completed = false;
        try
        {
            completed = _workerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            completed = _workerTask.IsCompleted;
        }

        if (completed)
        {
            _cts.Dispose();
        }
        else
        {
            _ = _workerTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _cts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
