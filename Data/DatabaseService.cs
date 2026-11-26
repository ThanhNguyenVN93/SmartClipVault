using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SmartClipVault.Models;

namespace SmartClipVault.Data;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly Lock _syncLock = new();

    public DatabaseService(string? dbPath = null)
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
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA secure_delete = ON;";
        pragma.ExecuteNonQuery();

        return connection;
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
                    SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId
                    FROM clips ORDER BY CreatedAt DESC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
            }
            else
            {
                var escaped = EscapeLikePattern(search.Trim());
                command.CommandText = """
                    SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId
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
                    CreatedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
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
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
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
                SELECT Id, ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId
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
            clips.Add(new ClipItem
            {
                Id = reader.GetInt64(0),
                ContentType = (ClipContentType)reader.GetInt32(1),
                TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                OcrText = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsSensitive = reader.GetInt32(5) != 0,
                CreatedAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                PinBoardId = reader.IsDBNull(7) ? null : reader.GetInt64(7)
            });
        }
        return clips;
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

        lock (_syncLock)
        {
            using var connection = OpenConnection();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips (ContentType, TextContent, ImagePath, OcrText, IsSensitive, CreatedAt, PinBoardId)
                VALUES ($contentType, $textContent, $imagePath, $ocrText, $isSensitive, $createdAt, $pinBoardId);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$contentType", (int)item.ContentType);
            command.Parameters.AddWithValue("$textContent", (object?)item.TextContent ?? DBNull.Value);
            command.Parameters.AddWithValue("$imagePath", (object?)item.ImagePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$ocrText", (object?)item.OcrText ?? DBNull.Value);
            command.Parameters.AddWithValue("$isSensitive", item.IsSensitive ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", createdAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$pinBoardId", (object?)item.PinBoardId ?? DBNull.Value);

            var result = command.ExecuteScalar();
            return result is long id ? id : Convert.ToInt64(result);
        }
    }

    public void UpdateOcrText(long id, string ocrText)
    {
        ArgumentNullException.ThrowIfNull(ocrText);
        if (id <= 0)
            return;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clips SET OcrText = $ocrText WHERE Id = $id;";
            command.Parameters.AddWithValue("$ocrText", ocrText);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteExpiredSensitiveClips(TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);
        if (maxAge >= DateTime.UtcNow - DateTime.MinValue)
            return;

        var cutoff = DateTime.UtcNow - maxAge;

        lock (_syncLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM clips WHERE IsSensitive = 1 AND CreatedAt < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            command.ExecuteNonQuery();
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
