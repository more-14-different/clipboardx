using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipboardX.Core;

/// <summary>
/// 剪贴板历史的 SQLite 持久化（与 Windows 版共用表结构，便于日后合并宿主）。
/// </summary>
public sealed class ClipboardHistoryStore
{
    private readonly string _dbPath;

    public ClipboardHistoryStore(string databaseFilePath)
    {
        _dbPath = databaseFilePath ?? throw new ArgumentNullException(nameof(databaseFilePath));
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            EnsureSchema();
        }
        catch
        {
            // 降级为仅内存历史（调用方仍可 TryInsert，多失败）
        }
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS clipboard_history (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              entry_type INTEGER NOT NULL,
              text_content TEXT,
              image_blob BLOB,
              image_w INTEGER NOT NULL DEFAULT 0,
              image_h INTEGER NOT NULL DEFAULT 0,
              file_paths_json TEXT,
              copied_at_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_history_copied ON clipboard_history(copied_at_ms DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private static long ToMs(DateTime dt) => new DateTimeOffset(dt).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>按时间从新到旧最多读取 limit 条。</summary>
    public List<HistoryEntry> LoadNewestFirst(int limit)
    {
        if (limit <= 0) return [];
        var list = new List<HistoryEntry>(Math.Min(limit, 64));
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, entry_type, text_content, image_blob, image_w, image_h, file_paths_json, copied_at_ms
                FROM clipboard_history
                ORDER BY copied_at_ms DESC
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadEntry(r));
        }
        catch
        {
            // ignore
        }
        return list;
    }

    /// <summary>仅保留按时间最新的 maxKeep 条，删除其余（与设置中的条数上限对齐）。</summary>
    public void PruneExcess(int maxKeep)
    {
        if (maxKeep < 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                DELETE FROM clipboard_history
                WHERE id NOT IN (
                  SELECT id FROM clipboard_history
                  ORDER BY copied_at_ms DESC
                  LIMIT @lim
                );
                """;
            cmd.Parameters.AddWithValue("@lim", maxKeep);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    private static HistoryEntry ReadEntry(SqliteDataReader r)
    {
        var entry = new HistoryEntry
        {
            PersistedId = r.GetInt64(0),
            Type = (EntryType)r.GetInt32(1),
            CopiedAt = FromMs(r.GetInt64(7)),
        };
        if (!r.IsDBNull(2)) entry.TextContent = r.GetString(2);
        if (!r.IsDBNull(3)) entry.ImageData = (byte[])r.GetValue(3);
        entry.ImageWidth = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        entry.ImageHeight = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        if (!r.IsDBNull(6))
        {
            var json = r.GetString(6);
            entry.FilePaths = JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        return entry;
    }

    /// <returns>是否成功写入并得到新 id</returns>
    public bool TryInsert(HistoryEntry entry)
    {
        try
        {
            var ms = ToMs(entry.CopiedAt);
            string? filesJson = entry.Type == EntryType.Files && entry.FilePaths is { Length: > 0 }
                ? JsonSerializer.Serialize(entry.FilePaths)
                : null;

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO clipboard_history (entry_type, text_content, image_blob, image_w, image_h, file_paths_json, copied_at_ms)
                VALUES (@t, @text, @blob, @w, @h, @files, @ms)
                """;
            cmd.Parameters.AddWithValue("@t", (int)entry.Type);
            cmd.Parameters.AddWithValue("@text", (object?)entry.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@blob", (object?)entry.ImageData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@w", entry.ImageWidth);
            cmd.Parameters.AddWithValue("@h", entry.ImageHeight);
            cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", ms);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            var idObj = cmd.ExecuteScalar();
            if (idObj is long lid) entry.PersistedId = lid;
            else if (idObj != null) entry.PersistedId = Convert.ToInt64(idObj);
            return entry.PersistedId.HasValue;
        }
        catch
        {
            return false;
        }
    }

    public void TryDelete(long? persistedId)
    {
        if (persistedId is not long id || id <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    public void TryUpdateCopiedAt(long persistedId, DateTime copiedAt)
    {
        if (persistedId <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_history SET copied_at_ms = @ms WHERE id = @id";
            cmd.Parameters.AddWithValue("@ms", ToMs(copiedAt));
            cmd.Parameters.AddWithValue("@id", persistedId);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }

    public void DeleteAll()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore
        }
    }
}
