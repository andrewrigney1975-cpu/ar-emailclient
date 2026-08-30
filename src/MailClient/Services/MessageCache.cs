using MailClient.Models;
using Microsoft.Data.Sqlite;

namespace MailClient.Services;

/// A thin SQLite cache of message-list rows so a folder shows its last-known contents instantly
/// while the live IMAP fetch runs. Bodies are not cached in this MVP.
public static class MessageCache
{
    private static readonly string DbPath = AppPaths.InData("cache.db");
    private static readonly object InitLock = new();
    private static bool _initialised;

    private static SqliteConnection Open()
    {
        EnsureSchema();
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    private static void EnsureSchema()
    {
        lock (InitLock)
        {
            if (_initialised)
            {
                return;
            }

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Summaries (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL,
                    FromName TEXT, FromAddr TEXT, Subject TEXT, Preview TEXT,
                    DateTicks INTEGER NOT NULL, HasAttachments INTEGER NOT NULL, IsRead INTEGER NOT NULL,
                    PRIMARY KEY (AccountId, Folder, Uid));

                CREATE TABLE IF NOT EXISTS Folders (
                    AccountId TEXT NOT NULL, FullName TEXT NOT NULL, Name TEXT NOT NULL,
                    Unread INTEGER NOT NULL, Ord INTEGER NOT NULL,
                    PRIMARY KEY (AccountId, FullName));

                CREATE INDEX IF NOT EXISTS IX_Summaries_Search
                    ON Summaries (AccountId, DateTicks DESC);
                """;
            cmd.ExecuteNonQuery();
            _initialised = true;
        }
    }

    public static List<MessageRow> Load(string accountId, string folder)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Uid, FromName, FromAddr, Subject, Preview, DateTicks, HasAttachments, IsRead " +
                              "FROM Summaries WHERE AccountId = @a AND Folder = @f ORDER BY DateTicks DESC";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MessageRow
                {
                    AccountId = accountId,
                    Folder = folder,
                    Uid = (uint)reader.GetInt64(0),
                    From = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    FromAddress = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Subject = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Preview = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Date = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    HasAttachments = reader.GetInt64(6) != 0,
                    IsRead = reader.GetInt64(7) != 0,
                });
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.Load", ex);
            return new List<MessageRow>();
        }
    }

    public static void Replace(string accountId, string folder, IReadOnlyList<MessageRow> rows)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM Summaries WHERE AccountId = @a AND Folder = @f";
                del.Parameters.AddWithValue("@a", accountId);
                del.Parameters.AddWithValue("@f", folder);
                del.ExecuteNonQuery();
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Summaries VALUES (@a, @f, @u, @fn, @fa, @s, @p, @d, @ha, @r)";
            foreach (var p in new[] { "@a", "@f", "@u", "@fn", "@fa", "@s", "@p", "@d", "@ha", "@r" })
            {
                ins.Parameters.Add(new SqliteParameter(p, null));
            }

            foreach (var row in rows)
            {
                ins.Parameters["@a"].Value = accountId;
                ins.Parameters["@f"].Value = folder;
                ins.Parameters["@u"].Value = (long)row.Uid;
                ins.Parameters["@fn"].Value = row.From;
                ins.Parameters["@fa"].Value = row.FromAddress;
                ins.Parameters["@s"].Value = row.Subject;
                ins.Parameters["@p"].Value = row.Preview;
                ins.Parameters["@d"].Value = row.Date.UtcTicks;
                ins.Parameters["@ha"].Value = row.HasAttachments ? 1 : 0;
                ins.Parameters["@r"].Value = row.IsRead ? 1 : 0;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.Replace", ex);
        }
    }

    public static void ClearAccount(string accountId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Summaries WHERE AccountId = @a; DELETE FROM Folders WHERE AccountId = @a;";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.ClearAccount", ex);
        }
    }

    // ----- folders -----

    public sealed record CachedFolder(string FullName, string Name, int Unread);

    public static List<CachedFolder> LoadFolders(string accountId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FullName, Name, Unread FROM Folders WHERE AccountId = @a ORDER BY Ord";
            cmd.Parameters.AddWithValue("@a", accountId);

            var rows = new List<CachedFolder>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CachedFolder(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.LoadFolders", ex);
            return new List<CachedFolder>();
        }
    }

    public static void SaveFolders(string accountId, IReadOnlyList<CachedFolder> folders)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM Folders WHERE AccountId = @a";
                del.Parameters.AddWithValue("@a", accountId);
                del.ExecuteNonQuery();
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Folders VALUES (@a, @fn, @n, @u, @o)";
            foreach (var p in new[] { "@a", "@fn", "@n", "@u", "@o" })
            {
                ins.Parameters.Add(new SqliteParameter(p, null));
            }

            for (var i = 0; i < folders.Count; i++)
            {
                ins.Parameters["@a"].Value = accountId;
                ins.Parameters["@fn"].Value = folders[i].FullName;
                ins.Parameters["@n"].Value = folders[i].Name;
                ins.Parameters["@u"].Value = folders[i].Unread;
                ins.Parameters["@o"].Value = i;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SaveFolders", ex);
        }
    }

    // ----- search -----

    /// Substring search over cached summaries for an account, newest first.
    public static List<MessageRow> Search(string accountId, string query, int limit = 200)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Uid, FromName, FromAddr, Subject, Preview, DateTicks, HasAttachments, IsRead, Folder " +
                "FROM Summaries WHERE AccountId = @a AND " +
                "(Subject LIKE @q OR FromName LIKE @q OR FromAddr LIKE @q OR Preview LIKE @q) " +
                "ORDER BY DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@q", "%" + query + "%");
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MessageRow
                {
                    AccountId = accountId,
                    Folder = reader.GetString(8),
                    Uid = (uint)reader.GetInt64(0),
                    From = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    FromAddress = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Subject = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Preview = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Date = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    HasAttachments = reader.GetInt64(6) != 0,
                    IsRead = reader.GetInt64(7) != 0,
                });
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.Search", ex);
            return new List<MessageRow>();
        }
    }

    public static void SetRead(string accountId, string folder, uint uid, bool read)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Summaries SET IsRead = @r WHERE AccountId = @a AND Folder = @f AND Uid = @u";
            cmd.Parameters.AddWithValue("@r", read ? 1 : 0);
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SetRead", ex);
        }
    }
}
