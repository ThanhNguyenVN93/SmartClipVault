using System;
using System.IO;
using System.Threading;
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
                """;
            command.ExecuteNonQuery();
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
}
