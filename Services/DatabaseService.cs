using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Flow.Plugin.ClipHistory.Models;

namespace Flow.Plugin.ClipHistory.Services;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public DatabaseService(string dbPath)
    {
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitDatabase();
    }

    private void InitDatabase()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA wal_autocheckpoint = 1000;
            ";
            pragmaCmd.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS clips (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type INTEGER NOT NULL,
                    content TEXT,
                    hash TEXT UNIQUE,
                    image_path TEXT,
                    thumbnail_path TEXT,
                    image_width INTEGER DEFAULT 0,
                    image_height INTEGER DEFAULT 0,
                    char_count INTEGER DEFAULT 0,
                    is_pinned INTEGER DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_clips_order ON clips(is_pinned DESC, updated_at DESC);
                CREATE INDEX IF NOT EXISTS idx_clips_hash ON clips(hash);
            ";
            cmd.ExecuteNonQuery();
        }
    }

    public List<ClipItem> LoadRecent(int limit = 1000)
    {
        var list = new List<ClipItem>(capacity: Math.Min(limit, 500));
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, type, content, hash, image_path, thumbnail_path, 
                       image_width, image_height, char_count, is_pinned, 
                       created_at, updated_at
                FROM clips
                ORDER BY is_pinned DESC, updated_at DESC
                LIMIT @limit;
            ";
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                list.Add(new ClipItem
                {
                    Id = reader.GetInt64(0),
                    Type = (ClipType)reader.GetInt32(1),
                    Content = content,
                    SearchText = content.ToLowerInvariant(),
                    Hash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ThumbnailPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ImageWidth = reader.GetInt32(6),
                    ImageHeight = reader.GetInt32(7),
                    CharCount = reader.GetInt32(8),
                    IsPinned = reader.GetInt32(9) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(10)),
                    UpdatedAt = DateTime.Parse(reader.GetString(11))
                });
            }
        }
        return list;
    }

    public ClipItem Upsert(ClipItem item, int maxHistoryLimit = 500)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();
            try
            {
                var nowStr = DateTime.Now.ToString("o");

                using var checkCmd = conn.CreateCommand();
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT id, is_pinned, image_path, thumbnail_path FROM clips WHERE hash = @hash LIMIT 1;";
                checkCmd.Parameters.AddWithValue("@hash", item.Hash);

                using (var reader = checkCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        item.Id = reader.GetInt64(0);
                        item.IsPinned = reader.GetInt32(1) == 1;
                        item.UpdatedAt = DateTime.Now;

                        using var updateCmd = conn.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = "UPDATE clips SET updated_at = @now WHERE id = @id;";
                        updateCmd.Parameters.AddWithValue("@now", nowStr);
                        updateCmd.Parameters.AddWithValue("@id", item.Id);
                        updateCmd.ExecuteNonQuery();

                        tx.Commit();
                        return item;
                    }
                }

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO clips (
                        type, content, hash, image_path, thumbnail_path,
                        image_width, image_height, char_count, is_pinned,
                        created_at, updated_at
                    ) VALUES (
                        @type, @content, @hash, @image_path, @thumbnail_path,
                        @image_width, @image_height, @char_count, @is_pinned,
                        @created_at, @updated_at
                    );
                    SELECT last_insert_rowid();
                ";
                insertCmd.Parameters.AddWithValue("@type", (int)item.Type);
                insertCmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@hash", item.Hash);
                insertCmd.Parameters.AddWithValue("@image_path", (object?)item.ImagePath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@thumbnail_path", (object?)item.ThumbnailPath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@image_width", item.ImageWidth);
                insertCmd.Parameters.AddWithValue("@image_height", item.ImageHeight);
                insertCmd.Parameters.AddWithValue("@char_count", item.CharCount);
                insertCmd.Parameters.AddWithValue("@is_pinned", item.IsPinned ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@created_at", nowStr);
                insertCmd.Parameters.AddWithValue("@updated_at", nowStr);

                item.Id = (long)(insertCmd.ExecuteScalar() ?? 0L);

                PruneLruInternal(conn, tx, maxHistoryLimit);

                tx.Commit();
                return item;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    private void PruneLruInternal(SqliteConnection conn, SqliteTransaction tx, int maxLimit)
    {
        using var selectOldCmd = conn.CreateCommand();
        selectOldCmd.Transaction = tx;
        selectOldCmd.CommandText = @"
            SELECT id, image_path, thumbnail_path 
            FROM clips 
            WHERE is_pinned = 0 
            ORDER BY updated_at DESC 
            LIMIT -1 OFFSET @maxLimit;
        ";
        selectOldCmd.Parameters.AddWithValue("@maxLimit", maxLimit);

        var idsToDelete = new List<long>();
        var filesToDelete = new List<string>();

        using (var reader = selectOldCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                idsToDelete.Add(reader.GetInt64(0));
                if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                if (!reader.IsDBNull(2)) filesToDelete.Add(reader.GetString(2));
            }
        }

        if (idsToDelete.Count > 0)
        {
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = $"DELETE FROM clips WHERE id IN ({string.Join(",", idsToDelete)});";
            delCmd.ExecuteNonQuery();

            DeleteAssociatedFilesSafe(filesToDelete);
        }
    }

    public void TogglePin(long id, bool isPinned)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clips SET is_pinned = @pinned, updated_at = @now WHERE id = @id;";
            cmd.Parameters.AddWithValue("@pinned", isPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        string? imgPath = null;
        string? thumbPath = null;

        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var queryCmd = conn.CreateCommand();
            queryCmd.CommandText = "SELECT image_path, thumbnail_path FROM clips WHERE id = @id;";
            queryCmd.Parameters.AddWithValue("@id", id);

            using (var reader = queryCmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    imgPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                    thumbPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM clips WHERE id = @id;";
            delCmd.Parameters.AddWithValue("@id", id);
            delCmd.ExecuteNonQuery();
        }

        DeleteAssociatedFilesSafe(new[] { imgPath, thumbPath });
    }

    public void ClearAll(bool preservePinned = true)
    {
        var filesToDelete = new List<string>();

        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var queryCmd = conn.CreateCommand();
            queryCmd.CommandText = preservePinned 
                ? "SELECT image_path, thumbnail_path FROM clips WHERE is_pinned = 0;"
                : "SELECT image_path, thumbnail_path FROM clips;";

            using (var reader = queryCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0)) filesToDelete.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                }
            }

            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = preservePinned 
                ? "DELETE FROM clips WHERE is_pinned = 0;"
                : "DELETE FROM clips;";
            delCmd.ExecuteNonQuery();
        }

        DeleteAssociatedFilesSafe(filesToDelete);
    }

    private static void CollectImageAndDerivedFiles(string? path, ISet<string> targetSet)
    {
        if (string.IsNullOrEmpty(path)) return;
        targetSet.Add(path);

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        var parentDir = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(parentDir)) return;

        var fileName = Path.GetFileNameWithoutExtension(path);
        string hash;
        if (fileName.StartsWith("img_")) hash = fileName.Substring(4);
        else if (fileName.StartsWith("thumb_")) hash = fileName.Substring(6);
        else if (fileName.StartsWith("prev_")) hash = fileName.Substring(5);
        else hash = fileName;

        targetSet.Add(Path.Combine(parentDir, "Images", $"img_{hash}.png"));
        targetSet.Add(Path.Combine(parentDir, "Thumbs", $"thumb_{hash}.png"));
        targetSet.Add(Path.Combine(parentDir, "Previews", $"prev_{hash}.png"));
    }

    private static void DeleteAssociatedFilesSafe(IEnumerable<string?> paths)
    {
        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            CollectImageAndDerivedFiles(p, allFiles);
        }

        if (allFiles.Count == 0) return;

        Task.Run(async () =>
        {
            foreach (var file in allFiles)
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(100);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch { }

        SqliteConnection.ClearAllPools();
    }
}
