using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SmartClipVault.Models;
using SmartClipVault.Services;

namespace SmartClipVault.Data;

public class DatabaseService
{
    private const string DpapiTextPrefix = "dpapi:";

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _connectionString;
    private readonly string _imageDirectory;
    private readonly Lock _syncLock = new();

    public event EventHandler? ClipsDeleted;

    public DatabaseService(string? dbPath = null, string? imageDirectory = null)
    {
        string path;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SmartClipVault");
            Directory.CreateDirectory(baseDir);
            path = Path.Combine(baseDir, "smartclipvault.db");
        }
        else
        {
            path = dbPath;
            if (!string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        };
        _connectionString = builder.ToString();
        _imageDirectory = string.IsNullOrWhiteSpace(imageDirectory)
            ? ClipboardMonitorService.ImageDirectory
            : imageDirectory;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA secure_delete = ON;";
            pragma.ExecuteNonQuery();

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        lock (_syncLock)
        {
            using var connection = OpenConnection();

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS clips (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ContentType INTEGER NOT NULL,
                    TextContent TEXT NULL,
                    ImagePath TEXT NULL,
                    OcrText TEXT NULL,
                    IsSensitive INTEGER NOT NULL DEFAULT 0,
                    IsStarred INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    PinBoardId INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS idx_clips_created_at ON clips(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS idx_clips_pin_board_id ON clips(PinBoardId);
                CREATE TABLE IF NOT EXISTS boards (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            EnsureClipsSchemaUpToDate(connection);
        }
    }

    private static void EnsureClipsSchemaUpToDate(SqliteConnection connection)
    {
        var hasIsStarred = false;
        var hasSourceApp = false;
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(clips);";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                    continue;
                var colName = reader.GetString(1);
                if (string.Equals(colName, "IsStarred", StringComparison.OrdinalIgnoreCase))
                    hasIsStarred = true;
                else if (string.Equals(colName, "SourceApp", StringComparison.OrdinalIgnoreCase))
                    hasSourceApp = true;
            }
        }

        if (!hasIsStarred)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE clips ADD COLUMN IsStarred INTEGER NOT NULL DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }

        if (!hasSourceApp)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE clips ADD COLUMN SourceApp TEXT NULL;";
            alterCmd.ExecuteNonQuery();
        }
    }

    public List<ClipItem> GetRecentClips(string? search, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            if (string.IsNullOrWhiteSpace(search))
            {
                command.CommandText = """
                    SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId, IsStarred, SourceApp
                    FROM clips ORDER BY CreatedAt DESC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
            }
            else
            {
                var escaped = EscapeLikePattern(search.Trim());
                command.CommandText = """
                    SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId, IsStarred, SourceApp
                    FROM clips
                    WHERE IsSensitive = 0
                      AND (TextContent LIKE $search ESCAPE '\' OR OcrText LIKE $search ESCAPE '\')
                    ORDER BY CreatedAt DESC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$search", $"%{escaped}%");
                command.Parameters.AddWithValue("$limit", limit);
            }

            return ReadClips(command);
        }
    }

    public Task<List<ClipItem>> GetRecentClipsAsync(string? search, int limit = 100, CancellationToken cancellationToken = default)
        => Task.Run(() => GetRecentClips(search, limit), cancellationToken);

    public List<BoardItem> GetBoards()
    {
        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, CreatedAt FROM boards ORDER BY CreatedAt DESC;";
            var boards = new List<BoardItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                boards.Add(new BoardItem
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    CreatedAt = ParseUtcDateTimeOrDefault(reader, 2)
                });
            }
            return boards;
        }
    }

    public Task<List<BoardItem>> GetBoardsAsync(CancellationToken cancellationToken = default)
        => Task.Run(GetBoards, cancellationToken);

    public long CreateBoard(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO boards (Name, CreatedAt) VALUES ($name, $createdAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var result = command.ExecuteScalar();
            return result is long id ? id : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    public Task<long> CreateBoardAsync(string name, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateBoard(name), cancellationToken);

    public void PinClipToBoard(long clipId, long? boardId)
    {
        if (clipId <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clips SET PinBoardId = $boardId WHERE Id = $id;";
            command.Parameters.AddWithValue("$boardId", (object?)boardId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", clipId);
            command.ExecuteNonQuery();
        }
    }

    public Task PinClipToBoardAsync(long clipId, long? boardId, CancellationToken cancellationToken = default)
        => Task.Run(() => PinClipToBoard(clipId, boardId), cancellationToken);

    public List<ClipItem> GetBoardClips(long boardId)
    {
        if (boardId <= 0)
            return [];

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId, IsStarred, SourceApp
                FROM clips WHERE PinBoardId = $boardId ORDER BY CreatedAt DESC;
                """;
            command.Parameters.AddWithValue("$boardId", boardId);
            return ReadClips(command);
        }
    }

    public Task<List<ClipItem>> GetBoardClipsAsync(long boardId, CancellationToken cancellationToken = default)
        => Task.Run(() => GetBoardClips(boardId), cancellationToken);

    private static string EscapeLikePattern(string value)
        => value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static List<ClipItem> ReadClips(SqliteCommand command)
    {
        var clips = new List<ClipItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var isSensitive = reader.GetInt32(5) != 0;
            var rawTextContent = reader.IsDBNull(2) ? null : reader.GetString(2);
            var rawOcrText = reader.IsDBNull(4) ? null : reader.GetString(4);

            clips.Add(new ClipItem
            {
                Id = reader.GetInt64(0),
                ContentType = (ClipContentType)reader.GetInt32(1),
                TextContent = isSensitive ? UnprotectSensitiveText(rawTextContent) : rawTextContent,
                ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                OcrText = isSensitive ? UnprotectSensitiveText(rawOcrText) : rawOcrText,
                IsSensitive = isSensitive,
                CreatedAt = ParseUtcDateTimeOrDefault(reader, 6),
                PinBoardId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                IsStarred = reader.FieldCount > 8 && !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
                SourceApp = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null
            });
        }
        return clips;
    }

    private static DateTime ParseUtcDateTimeOrDefault(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        var raw = reader.GetString(ordinal);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    }

    private static string? ProtectSensitiveText(string? value)
    {
        if (value is null)
            return null;

        if (value.StartsWith(DpapiTextPrefix, StringComparison.Ordinal))
            return value;

        var plainBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return DpapiTextPrefix + Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string? UnprotectSensitiveText(string? value)
    {
        if (value is null)
            return null;

        if (!value.StartsWith(DpapiTextPrefix, StringComparison.Ordinal))
            return value;

        try
        {
            var cipherBytes = Convert.FromBase64String(value[DpapiTextPrefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    public long InsertClip(ClipItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var createdAtUtc = item.CreatedAt.Kind switch
        {
            DateTimeKind.Utc => item.CreatedAt,
            DateTimeKind.Local => item.CreatedAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc)
        };

        var storedTextContent = item.IsSensitive ? ProtectSensitiveText(item.TextContent) : item.TextContent;
        var storedOcrText = item.IsSensitive ? ProtectSensitiveText(item.OcrText) : item.OcrText;

        lock (_syncLock)
        {
            using var connection = OpenConnection();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (ContentType, TextContent, ImagePath, OcrText, IsSensitive, IsStarred, CreatedAt, PinBoardId, SourceApp)
                VALUES ($contentType, $textContent, $imagePath, $ocrText, $isSensitive, $isStarred, $createdAt, $pinBoardId, $sourceApp);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$contentType", (int)item.ContentType);
            command.Parameters.AddWithValue("$textContent", (object?)storedTextContent ?? DBNull.Value);
            command.Parameters.AddWithValue("$imagePath", (object?)item.ImagePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$ocrText", (object?)storedOcrText ?? DBNull.Value);
            command.Parameters.AddWithValue("$isSensitive", item.IsSensitive ? 1 : 0);
            command.Parameters.AddWithValue("$isStarred", item.IsStarred ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$pinBoardId", (object?)item.PinBoardId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sourceApp", (object?)item.SourceApp ?? DBNull.Value);

            var result = command.ExecuteScalar();
            return result is long id ? id : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    public void SetClipStarred(long id, bool isStarred)
    {
        if (id <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clips SET IsStarred = $isStarred WHERE Id = $id;";
            command.Parameters.AddWithValue("$isStarred", isStarred ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateOcrText(long id, string ocrText)
        => UpdateOcrText(id, ocrText, isSensitive: false);

    public void UpdateOcrText(long id, string ocrText, bool isSensitive)
    {
        ArgumentNullException.ThrowIfNull(ocrText);
        if (id <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            bool currentlySensitive;
            string? existingTextContent;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT IsSensitive, TextContent FROM clips WHERE Id = $id;";
                select.Parameters.AddWithValue("$id", id);
                using var reader = select.ExecuteReader();
                if (!reader.Read())
                    return;

                currentlySensitive = reader.GetInt32(0) != 0;
                existingTextContent = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var finalSensitive = currentlySensitive || isSensitive;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                if (finalSensitive)
                {
                    var protectedOcr = ProtectSensitiveText(ocrText);
                    var protectedText = !currentlySensitive && existingTextContent is not null
                        ? ProtectSensitiveText(existingTextContent)
                        : existingTextContent;
                    command.CommandText = """
                        UPDATE clips
                        SET OcrText = $ocrText, IsSensitive = 1, TextContent = $textContent
                        WHERE Id = $id;
                        """;
                    command.Parameters.AddWithValue("$ocrText", (object?)protectedOcr ?? DBNull.Value);
                    command.Parameters.AddWithValue("$textContent", (object?)protectedText ?? DBNull.Value);
                }
                else
                {
                    command.CommandText = "UPDATE clips SET OcrText = $ocrText WHERE Id = $id;";
                    command.Parameters.AddWithValue("$ocrText", ocrText);
                }

                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void DeleteRecentUnpinnedClips(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        if (window == TimeSpan.Zero)
            return;

        var maxWindow = DateTime.UtcNow - DateTime.MinValue;
        var cutoff = window >= maxWindow ? DateTime.MinValue : DateTime.UtcNow - window;
        var cutoffStr = cutoff.ToString("O", CultureInfo.InvariantCulture);
        var imagePaths = new List<string>();
        int deletedCount;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT ImagePath FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0 AND CreatedAt >= $cutoff AND ImagePath IS NOT NULL;";
                select.Parameters.AddWithValue("$cutoff", cutoffStr);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        imagePaths.Add(reader.GetString(0));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "DELETE FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0 AND CreatedAt >= $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoffStr);
                deletedCount = command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeleteImageFilesSafely(imagePaths);
        if (deletedCount > 0)
            ClipsDeleted?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUnpinnedHistory()
    {
        var imagePaths = new List<string>();
        int deletedCount;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT ImagePath FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0 AND ImagePath IS NOT NULL;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        imagePaths.Add(reader.GetString(0));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "DELETE FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0;";
                deletedCount = command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeleteImageFilesSafely(imagePaths);
        if (deletedCount > 0)
            ClipsDeleted?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteExpiredUnpinnedClips(int days)
    {
        if (days <= 0 || days >= (DateTime.UtcNow - DateTime.MinValue).TotalDays)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var cutoffStr = cutoff.ToString("O", CultureInfo.InvariantCulture);
        var imagePaths = new List<string>();
        int deletedCount;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT ImagePath FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0 AND CreatedAt < $cutoff AND ImagePath IS NOT NULL;";
                select.Parameters.AddWithValue("$cutoff", cutoffStr);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        imagePaths.Add(reader.GetString(0));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "DELETE FROM clips WHERE PinBoardId IS NULL AND IsStarred = 0 AND CreatedAt < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoffStr);
                deletedCount = command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeleteImageFilesSafely(imagePaths);
        if (deletedCount > 0)
            ClipsDeleted?.Invoke(this, EventArgs.Empty);
    }

    public Task DeleteExpiredUnpinnedClipsAsync(int days, System.Threading.CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.Run(() => DeleteExpiredUnpinnedClips(days), cancellationToken);

    public void DeleteClip(long id)
    {
        if (id <= 0)
            return;

        string? imagePath;
        int deletedCount;
        lock (_syncLock)
        {
            using var connection = OpenConnection();

            string? path = null;
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT ImagePath FROM clips WHERE Id = $id;";
                selectCmd.Parameters.AddWithValue("$id", id);
                path = selectCmd.ExecuteScalar() as string;
            }

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.CommandText = "DELETE FROM clips WHERE Id = $id;";
                deleteCmd.Parameters.AddWithValue("$id", id);
                deletedCount = deleteCmd.ExecuteNonQuery();
            }

            imagePath = path;
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
            DeleteImageFilesSafely([imagePath]);
        if (deletedCount > 0)
            ClipsDeleted?.Invoke(this, EventArgs.Empty);
    }

    public Task DeleteClipAsync(long id, CancellationToken cancellationToken = default)
        => Task.Run(() => DeleteClip(id), cancellationToken);

    public void UnpinClip(long id)
    {
        if (id <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clips SET PinBoardId = NULL WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public Task UnpinClipAsync(long id, CancellationToken cancellationToken = default)
        => Task.Run(() => UnpinClip(id), cancellationToken);

    public void UpdateClipText(long id, string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);
        if (id <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            bool isSensitive;
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = "SELECT IsSensitive FROM clips WHERE Id = $id;";
                selectCmd.Parameters.AddWithValue("$id", id);
                var result = selectCmd.ExecuteScalar();
                if (result is null or DBNull)
                    return;

                isSensitive = Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
            }

            var storedText = isSensitive ? ProtectSensitiveText(newText) : newText;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "UPDATE clips SET TextContent = $text WHERE Id = $id;";
                command.Parameters.AddWithValue("$text", (object?)storedText ?? DBNull.Value);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public Task UpdateClipTextAsync(long id, string newText, CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateClipText(id, newText), cancellationToken);

    public void DeleteExpiredSensitiveClips(TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);
        if (maxAge >= DateTime.UtcNow - DateTime.MinValue)
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        var cutoffStr = cutoff.ToString("O", CultureInfo.InvariantCulture);
        var imagePaths = new List<string>();
        int deletedCount;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT ImagePath FROM clips WHERE IsSensitive = 1 AND CreatedAt < $cutoff AND ImagePath IS NOT NULL;";
                select.Parameters.AddWithValue("$cutoff", cutoffStr);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        imagePaths.Add(reader.GetString(0));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "DELETE FROM clips WHERE IsSensitive = 1 AND CreatedAt < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoffStr);
                deletedCount = command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeleteImageFilesSafely(imagePaths);
        if (deletedCount > 0)
            ClipsDeleted?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteImageFilesSafely(IEnumerable<string> imagePaths)
    {
        string normalizedBaseNoSlash;
        try
        {
            var fullBaseDir = Path.GetFullPath(_imageDirectory);
            var baseDirInfo = new DirectoryInfo(fullBaseDir);
            if (!baseDirInfo.Exists || (baseDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return;

            normalizedBaseNoSlash = fullBaseDir
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }

        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                if (!PasteHelper.TryNormalizeContainedImagePath(path, out var candidate, _imageDirectory))
                    continue;

                if (PasteHelper.HasReparsePointSubdirectory(candidate, normalizedBaseNoSlash))
                    continue;

                var fileInfo = new FileInfo(candidate);
                if (!fileInfo.Exists ||
                    (fileInfo.Attributes & FileAttributes.Directory) != 0 ||
                    (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                File.Delete(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    /// <summary>Copies the live database to <paramref name="destPath"/> using SQLite online backup.</summary>
    public void BackupTo(string destPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        lock (_syncLock)
        {
            using var src = OpenConnection();
            var destConnString = new SqliteConnectionStringBuilder
            {
                DataSource = destPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using var dest = new SqliteConnection(destConnString);
            try
            {
                dest.Open();
                src.BackupDatabase(dest);
            }
            finally
            {
                dest.Close();
                SqliteConnection.ClearPool(dest);
            }
        }
    }

    internal void ValidateBackupFile(string srcPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcPath);
        if (!File.Exists(srcPath))
            throw new FileNotFoundException("Backup database file was not found.", srcPath);

        var srcConnString = new SqliteConnectionStringBuilder
        {
            DataSource = srcPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var src = new SqliteConnection(srcConnString);
        try
        {
            src.Open();
            ValidateBackupDatabase(src);
        }
        finally
        {
            src.Close();
            SqliteConnection.ClearPool(src);
        }
    }

    /// <summary>Replaces the live database with the contents of <paramref name="srcPath"/>.</summary>
    public void RestoreFrom(string srcPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcPath);
        if (!File.Exists(srcPath))
            throw new FileNotFoundException("Backup database file was not found.", srcPath);

        lock (_syncLock)
        {
            var srcConnString = new SqliteConnectionStringBuilder
            {
                DataSource = srcPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var src = new SqliteConnection(srcConnString);
            try
            {
                src.Open();
                ValidateBackupDatabase(src);

                // Clear pooled connections to the live DB so BackupDatabase cannot hit
                // stale handles or locks and future callers observe the restored state.
                using (var poolHandle = new SqliteConnection(_connectionString))
                {
                    SqliteConnection.ClearPool(poolHandle);
                }

                using var dest = OpenConnection();
                try
                {
                    SqliteConnection.ClearPool(dest);
                    src.BackupDatabase(dest);
                    EnsureClipsSchemaUpToDate(dest);
                    RemapClipImagePaths(dest, _imageDirectory);
                }
                finally
                {
                    dest.Close();
                    SqliteConnection.ClearPool(dest);
                }
            }
            finally
            {
                src.Close();
                SqliteConnection.ClearPool(src);
            }
        }
    }

    private static void RemapClipImagePaths(SqliteConnection connection, string imageDirectory)
    {
        var fullImageDir = Path.GetFullPath(imageDirectory);
        var updates = new List<(long Id, string? NewPath)>();

        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT Id, ImagePath FROM clips WHERE ImagePath IS NOT NULL;";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var oldPath = reader.GetString(1);
                var remapped = RemapSingleImagePath(oldPath, fullImageDir);
                if (!string.Equals(oldPath, remapped, StringComparison.Ordinal))
                    updates.Add((id, remapped));
            }
        }

        if (updates.Count == 0)
            return;

        using var tx = connection.BeginTransaction();
        using var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = "UPDATE clips SET ImagePath = $imagePath WHERE Id = $id;";
        var pathParam = updateCmd.CreateParameter();
        pathParam.ParameterName = "$imagePath";
        updateCmd.Parameters.Add(pathParam);
        var idParam = updateCmd.CreateParameter();
        idParam.ParameterName = "$id";
        updateCmd.Parameters.Add(idParam);

        foreach (var (id, newPath) in updates)
        {
            pathParam.Value = (object?)newPath ?? DBNull.Value;
            idParam.Value = id;
            updateCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static string? RemapSingleImagePath(string oldPath, string fullImageDir)
    {
        if (string.IsNullOrWhiteSpace(oldPath))
            return null;

        var normalizedOld = oldPath.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedOld))
            return null;

        const string imagesMarker = "/images/";
        var markerIndex = normalizedOld.LastIndexOf(imagesMarker, StringComparison.OrdinalIgnoreCase);
        string relativePath;
        if (markerIndex >= 0)
        {
            relativePath = normalizedOld[(markerIndex + imagesMarker.Length)..];
        }
        else if (normalizedOld.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalizedOld["images/".Length..];
        }
        else
        {
            var lastSlash = normalizedOld.LastIndexOf('/');
            relativePath = lastSlash >= 0 ? normalizedOld[(lastSlash + 1)..] : normalizedOld;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0') || relativePath.Contains(':'))
            return null;

        var invalidFileChars = Path.GetInvalidFileNameChars();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.IndexOfAny(invalidFileChars) >= 0)
            {
                return null;
            }

            var firstDot = segment.IndexOf('.');
            var baseName = firstDot >= 0 ? segment[..firstDot] : segment;
            if (WindowsReservedDeviceNames.Contains(baseName))
                return null;
        }

        var candidate = Path.Combine(fullImageDir, string.Join(Path.DirectorySeparatorChar, segments));
        return PasteHelper.TryNormalizeContainedImagePath(candidate, out var normalized, fullImageDir)
            ? normalized
            : null;
    }

    private static void ValidateBackupDatabase(SqliteConnection connection)
    {
        try
        {
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "PRAGMA quick_check;";
                using var reader = checkCmd.ExecuteReader();
                if (!reader.Read() ||
                    !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) ||
                    reader.Read())
                {
                    throw new InvalidDataException("Backup database failed integrity check.");
                }
            }

            using (var unsafeObjCmd = connection.CreateCommand())
            {
                unsafeObjCmd.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE LOWER(type) IN ('trigger', 'view');
                    """;
                var unsafeCount = Convert.ToInt64(unsafeObjCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (unsafeCount > 0)
                    throw new InvalidDataException("Backup database contains disallowed views or triggers.");
            }

            using (var tableCmd = connection.CreateCommand())
            {
                tableCmd.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name IN ('clips', 'boards');
                    """;
                var tableCount = Convert.ToInt64(tableCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (tableCount != 2)
                    throw new InvalidDataException("Backup database is missing required tables.");
            }

            foreach (var sql in new[]
                     {
                         "SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId FROM clips LIMIT 0;",
                         "SELECT Id, Name, CreatedAt FROM boards LIMIT 0;"
                     })
            {
                using var schemaCmd = connection.CreateCommand();
                schemaCmd.CommandText = sql;
                using var schemaReader = schemaCmd.ExecuteReader();
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"Backup database is invalid or corrupt: {ex.Message}", ex);
        }
    }
}
