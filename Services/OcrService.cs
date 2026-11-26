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
    private const uint MaxImageDimension = 16384;
    private const ulong MaxImagePixels = 64_000_000;

    private readonly DatabaseService _databaseService;
    private readonly string? _allowedImageDirectory;
    private readonly string? _normalizedAllowedDirNoSlash;
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
            _allowedImageDirectory = allowedImageDirectory;
            _normalizedAllowedDirNoSlash = Path.GetFullPath(allowedImageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
        try
        {
            var token = _cts.Token;
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                Trace.TraceWarning("OcrService: no OCR engine available for user profile languages.");
                return;
            }

            await foreach (var item in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (!TryGetValidatedImagePath(item.ImagePath, out var validatedPath))
                    continue;

                try
                {
                    var ocrText = await ExtractOcrTextAsync(engine, validatedPath, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(ocrText))
                    {
                        var isSensitive = SecretDetector.IsSensitive(ocrText);
                        if (isSensitive)
                            item.IsSensitive = true;

                        _databaseService.UpdateOcrText(item.Id, ocrText, isSensitive);
                    }
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
        catch (Exception ex)
        {
            Trace.TraceError($"OcrService: worker loop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private bool TryGetValidatedImagePath(string? imagePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
            return false;

        try
        {
            string candidate;
            if (_allowedImageDirectory is not null && _normalizedAllowedDirNoSlash is not null)
            {
                if (!PasteHelper.TryNormalizeContainedImagePath(imagePath, out candidate, _allowedImageDirectory))
                    return false;

                var baseDirInfo = new DirectoryInfo(_normalizedAllowedDirNoSlash);
                if (baseDirInfo.Exists && (baseDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;

                if (PasteHelper.HasReparsePointSubdirectory(candidate, _normalizedAllowedDirNoSlash))
                    return false;
            }
            else
            {
                var root = Path.GetPathRoot(imagePath);
                if (string.IsNullOrEmpty(root) ||
                    !PasteHelper.TryNormalizeContainedImagePath(imagePath, out candidate, root))
                {
                    return false;
                }

                var normalizedRootNoSlash = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (PasteHelper.HasReparsePointSubdirectory(candidate, normalizedRootNoSlash))
                    return false;
            }

            var fileInfo = new FileInfo(candidate);
            if (fileInfo.Exists &&
                ((fileInfo.Attributes & FileAttributes.Directory) != 0 ||
                 (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> ExtractOcrTextAsync(OcrEngine engine, string imagePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(imagePath);
        if (!fileInfo.Exists ||
            (fileInfo.Attributes & FileAttributes.Directory) != 0 ||
            (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return string.Empty;
        }

        using var fileStream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var stream = fileStream.AsRandomAccessStream();

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var pixelWidth = decoder.PixelWidth;
        var pixelHeight = decoder.PixelHeight;
        if (pixelWidth == 0 || pixelHeight == 0 ||
            pixelWidth > MaxImageDimension || pixelHeight > MaxImageDimension ||
            (ulong)pixelWidth * pixelHeight > MaxImagePixels)
        {
            return string.Empty;
        }

        var transform = CreateScaledTransformIfNeeded(pixelWidth, pixelHeight);

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
        if (width == 0 || height == 0 || maxDim == 0 || (width <= maxDim && height <= maxDim))
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
