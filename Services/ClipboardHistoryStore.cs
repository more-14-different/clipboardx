using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipboardManager;

/// <summary>
/// 剪贴板普通历史（不含快捷短语）的 SQLite 持久化。库路径：%LocalAppData%\ClipboardX\clipboard_history.db
/// </summary>
internal sealed class ClipboardHistoryStore
{
    private static string DbDir => Path.GetDirectoryName(AppPaths.SqliteDbFile)!;
    private static string DbPath => AppPaths.SqliteDbFile;

    private static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public ClipboardHistoryStore()
    {
        try
        {
            Directory.CreateDirectory(DbDir);
            EnsureSchema();
        }
        catch
        {
            // 降级为仅内存历史
        }
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        try
        {
            cmd.CommandText = "ALTER TABLE clipboard_history ADD COLUMN pinyin_blob TEXT;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Column likely exists, ignore
        }

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
              copied_at_ms INTEGER NOT NULL,
              pinyin_blob TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_history_copied ON clipboard_history(copied_at_ms DESC);

            CREATE VIRTUAL TABLE IF NOT EXISTS clipboard_history_fts USING fts5(
              text_content,
              pinyin_blob,
              content='clipboard_history',
              content_rowid='id',
              tokenize='trigram'
            );

            CREATE TRIGGER IF NOT EXISTS t_clipboard_history_ai AFTER INSERT ON clipboard_history BEGIN
              INSERT INTO clipboard_history_fts(rowid, text_content, pinyin_blob)
              VALUES (new.id, new.text_content, new.pinyin_blob);
            END;
            CREATE TRIGGER IF NOT EXISTS t_clipboard_history_ad AFTER DELETE ON clipboard_history BEGIN
              INSERT INTO clipboard_history_fts(clipboard_history_fts, rowid, text_content, pinyin_blob)
              VALUES ('delete', old.id, old.text_content, old.pinyin_blob);
            END;
            CREATE TRIGGER IF NOT EXISTS t_clipboard_history_au AFTER UPDATE ON clipboard_history BEGIN
              INSERT INTO clipboard_history_fts(clipboard_history_fts, rowid, text_content, pinyin_blob)
              VALUES ('delete', old.id, old.text_content, old.pinyin_blob);
              INSERT INTO clipboard_history_fts(rowid, text_content, pinyin_blob)
              VALUES (new.id, new.text_content, new.pinyin_blob);
            END;
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
    public List<ClipboardEntry> LoadNewestFirst(int limit)
    {
        return Search("", null, limit);
    }

    /// <summary>利用 FTS5 和类型过滤执行检索</summary>
    public List<ClipboardEntry> Search(string query, EntryType? typeFilter, int limit)
    {
        if (limit <= 0) return [];
        var list = new List<ClipboardEntry>(Math.Min(limit, 64));
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var sql = "SELECT c.id, c.entry_type, c.text_content, c.image_blob, c.image_w, c.image_h, c.file_paths_json, c.copied_at_ms FROM clipboard_history c";
            
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            bool useFts = hasQuery && query.Length >= 3;
            bool useLike = hasQuery && query.Length < 3;

            if (useFts)
            {
                sql += " JOIN clipboard_history_fts f ON c.id = f.rowid";
            }
            
            var whereClauses = new List<string>();
            if (useFts)
            {
                whereClauses.Add("clipboard_history_fts MATCH @q");
            }
            else if (useLike)
            {
                whereClauses.Add("(c.text_content LIKE @likeQ OR c.pinyin_blob LIKE @likeQ)");
            }
            
            if (typeFilter.HasValue)
            {
                if (typeFilter.Value == EntryType.Image)
                    whereClauses.Add("(c.entry_type = 1 OR c.entry_type = 2)"); // Image or Files (we'll filter image files strictly in C#)
                else
                    whereClauses.Add("c.entry_type = @t");
            }
            
            if (whereClauses.Count > 0)
            {
                sql += " WHERE " + string.Join(" AND ", whereClauses);
            }
            
            sql += " ORDER BY c.copied_at_ms DESC LIMIT @lim";
            cmd.CommandText = sql;
            
            if (useFts)
            {
                string ftsQuery = $"\"{query.Replace("\"", "\"\"")}\"";
                cmd.Parameters.AddWithValue("@q", ftsQuery);
            }
            else if (useLike)
            {
                cmd.Parameters.AddWithValue("@likeQ", $"%{query}%");
            }
            
            if (typeFilter.HasValue && typeFilter.Value != EntryType.Image)
            {
                cmd.Parameters.AddWithValue("@t", (int)typeFilter.Value);
            }
            
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

    private static ClipboardEntry ReadEntry(SqliteDataReader r)
    {
        var entry = new ClipboardEntry
        {
            PersistedId = r.GetInt64(0),
            Type = (EntryType)r.GetInt32(1),
            CopiedAt = FromMs(r.GetInt64(7)),
            IsQuickPaste = false
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
    public bool TryInsert(ClipboardEntry entry)
    {
        if (entry.IsQuickPaste) return false;
        try
        {
            var ms = ToMs(entry.CopiedAt);
            string? filesJson = entry.Type == EntryType.Files && entry.FilePaths is { Length: > 0 }
                ? JsonSerializer.Serialize(entry.FilePaths)
                : null;
                
            string pinyinBlob = "";
            if (entry.Type != EntryType.Image)
                pinyinBlob = PinyinSearchIndex.BuildBlob(entry.SearchableText);

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO clipboard_history (entry_type, text_content, image_blob, image_w, image_h, file_paths_json, copied_at_ms, pinyin_blob)
                VALUES (@t, @text, @blob, @w, @h, @files, @ms, @py)
                """;
            cmd.Parameters.AddWithValue("@t", (int)entry.Type);
            cmd.Parameters.AddWithValue("@text", (object?)entry.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@blob", (object?)entry.ImageData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@w", entry.ImageWidth);
            cmd.Parameters.AddWithValue("@h", entry.ImageHeight);
            cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", ms);
            cmd.Parameters.AddWithValue("@py", pinyinBlob);
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

    /// <summary>就地更新文本条目的内容（entry_type 仍为文本）。</summary>
    public void TryUpdateText(long persistedId, string text)
    {
        if (persistedId <= 0) return;
        try
        {
            var py = PinyinSearchIndex.BuildBlob(text);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE clipboard_history SET text_content = @t, pinyin_blob = @py WHERE id = @id AND entry_type = @et";
            cmd.Parameters.AddWithValue("@t", text);
            cmd.Parameters.AddWithValue("@py", py);
            cmd.Parameters.AddWithValue("@id", persistedId);
            cmd.Parameters.AddWithValue("@et", (int)EntryType.Text);
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
