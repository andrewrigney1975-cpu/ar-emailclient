using System.Text.RegularExpressions;
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
                    Priority INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (AccountId, Folder, Uid));

                CREATE TABLE IF NOT EXISTS Follows (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL,
                    DueTicks INTEGER NOT NULL, EventId TEXT NOT NULL, Done INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (AccountId, Folder, Uid));

                CREATE TABLE IF NOT EXISTS Folders (
                    AccountId TEXT NOT NULL, FullName TEXT NOT NULL, Name TEXT NOT NULL,
                    Unread INTEGER NOT NULL, Ord INTEGER NOT NULL, Role TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (AccountId, FullName));

                CREATE TABLE IF NOT EXISTS Favourites (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL,
                    PRIMARY KEY (AccountId, Folder, Uid));

                CREATE TABLE IF NOT EXISTS Tags (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL, Tag TEXT NOT NULL,
                    PRIMARY KEY (AccountId, Folder, Uid, Tag));

                CREATE INDEX IF NOT EXISTS IX_Summaries_Search
                    ON Summaries (AccountId, DateTicks DESC);

                CREATE INDEX IF NOT EXISTS IX_Tags_Tag ON Tags (Tag);

                CREATE TABLE IF NOT EXISTS AiSummaries (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL,
                    Summary TEXT NOT NULL, EventTicks INTEGER NOT NULL DEFAULT 0,
                    EventTitle TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (AccountId, Folder, Uid));

                CREATE TABLE IF NOT EXISTS AiReplies (
                    AccountId TEXT NOT NULL, Folder TEXT NOT NULL, Uid INTEGER NOT NULL,
                    Json TEXT NOT NULL,
                    PRIMARY KEY (AccountId, Folder, Uid));
                """;
            cmd.ExecuteNonQuery();

            // Migrate older tables that predate added columns.
            foreach (var alter in new[]
                     {
                         "ALTER TABLE Folders ADD COLUMN Role TEXT NOT NULL DEFAULT ''",
                         "ALTER TABLE Summaries ADD COLUMN Priority INTEGER NOT NULL DEFAULT 1",
                         "ALTER TABLE AiSummaries ADD COLUMN EventTicks INTEGER NOT NULL DEFAULT 0",
                         "ALTER TABLE AiSummaries ADD COLUMN EventTitle TEXT NOT NULL DEFAULT ''",
                     })
            {
                try
                {
                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = alter;
                    cmd2.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // column already exists
                }
            }

            _initialised = true;
        }
    }

    public static List<MessageRow> Load(string accountId, string folder)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Uid, FromName, FromAddr, Subject, Preview, DateTicks, HasAttachments, IsRead, Priority " +
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
                    Priority = (int)reader.GetInt64(8),
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
            ins.CommandText = "INSERT INTO Summaries VALUES (@a, @f, @u, @fn, @fa, @s, @p, @d, @ha, @r, @pr)";
            foreach (var p in new[] { "@a", "@f", "@u", "@fn", "@fa", "@s", "@p", "@d", "@ha", "@r", "@pr" })
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
                ins.Parameters["@pr"].Value = row.Priority;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.Replace", ex);
        }
    }

    /// Forgets a single message everywhere in the cache (used after a move/expunge).
    public static void RemoveMessage(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Summaries WHERE AccountId=@a AND Folder=@f AND Uid=@u; " +
                "DELETE FROM Tags WHERE AccountId=@a AND Folder=@f AND Uid=@u; " +
                "DELETE FROM Favourites WHERE AccountId=@a AND Folder=@f AND Uid=@u; " +
                "DELETE FROM Follows WHERE AccountId=@a AND Folder=@f AND Uid=@u; " +
                "DELETE FROM AiSummaries WHERE AccountId=@a AND Folder=@f AND Uid=@u; " +
                "DELETE FROM AiReplies WHERE AccountId=@a AND Folder=@f AND Uid=@u;";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.RemoveMessage", ex);
        }
    }

    public static void RemoveFolder(string accountId, string folder)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Summaries WHERE AccountId=@a AND Folder=@f; " +
                "DELETE FROM Folders WHERE AccountId=@a AND FullName=@f;";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.RemoveFolder", ex);
        }
    }

    public static void ClearAccount(string accountId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Summaries WHERE AccountId = @a; DELETE FROM Folders WHERE AccountId = @a; " +
                "DELETE FROM Favourites WHERE AccountId = @a; DELETE FROM Tags WHERE AccountId = @a; " +
                "DELETE FROM Follows WHERE AccountId = @a; DELETE FROM AiSummaries WHERE AccountId = @a; " +
                "DELETE FROM AiReplies WHERE AccountId = @a;";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.ClearAccount", ex);
        }
    }

    // ----- folders -----

    public sealed record CachedFolder(string FullName, string Name, int Unread, string Role = "");

    public static List<CachedFolder> LoadFolders(string accountId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FullName, Name, Unread, Role FROM Folders WHERE AccountId = @a ORDER BY Ord";
            cmd.Parameters.AddWithValue("@a", accountId);

            var rows = new List<CachedFolder>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CachedFolder(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
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
            ins.CommandText = "INSERT INTO Folders VALUES (@a, @fn, @n, @u, @o, @r)";
            foreach (var p in new[] { "@a", "@fn", "@n", "@u", "@o", "@r" })
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
                ins.Parameters["@r"].Value = folders[i].Role;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SaveFolders", ex);
        }
    }

    /// Every unread cached message across all accounts and folders, newest first.
    public static List<MessageRow> LoadUnread(int limit = 500)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT AccountId, Folder, Uid, FromName, FromAddr, Subject, Preview, DateTicks, HasAttachments, Priority " +
                "FROM Summaries WHERE IsRead = 0 ORDER BY DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MessageRow
                {
                    AccountId = reader.GetString(0),
                    Folder = reader.GetString(1),
                    Uid = (uint)reader.GetInt64(2),
                    From = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    FromAddress = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Subject = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    Preview = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    Date = new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
                    HasAttachments = reader.GetInt64(8) != 0,
                    IsRead = false,
                    Priority = (int)reader.GetInt64(9),
                });
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.LoadUnread", ex);
            return new List<MessageRow>();
        }
    }

    private static MessageRow ReadFullRow(SqliteDataReader r) => new()
    {
        AccountId = r.GetString(0),
        Folder = r.GetString(1),
        Uid = (uint)r.GetInt64(2),
        From = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        FromAddress = r.IsDBNull(4) ? string.Empty : r.GetString(4),
        Subject = r.IsDBNull(5) ? string.Empty : r.GetString(5),
        Preview = r.IsDBNull(6) ? string.Empty : r.GetString(6),
        Date = new DateTimeOffset(r.GetInt64(7), TimeSpan.Zero),
        HasAttachments = r.GetInt64(8) != 0,
        IsRead = r.GetInt64(9) != 0,
        Priority = (int)r.GetInt64(10),
    };

    private const string FullRowColumns =
        "s.AccountId, s.Folder, s.Uid, s.FromName, s.FromAddr, s.Subject, s.Preview, " +
        "s.DateTicks, s.HasAttachments, s.IsRead, s.Priority";

    /// Cross-account messages in every folder tagged with a SPECIAL-USE role ("inbox", "sent", …).
    public static List<MessageRow> LoadByRole(string role, int limit = 500)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT {FullRowColumns} FROM Summaries s " +
                "JOIN Folders f ON f.AccountId = s.AccountId AND f.FullName = s.Folder " +
                "WHERE f.Role = @role ORDER BY s.DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@role", role);
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadFullRow(reader));
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.LoadByRole", ex);
            return new List<MessageRow>();
        }
    }

    // ----- favourites -----

    public static event EventHandler? FavouritesChanged;

    public static HashSet<string> FavouriteKeys()
    {
        var set = new HashSet<string>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT AccountId, Folder, Uid FROM Favourites";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                set.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt64(2)}");
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.FavouriteKeys", ex);
        }

        return set;
    }

    public static bool IsFavourite(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Favourites WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            return cmd.ExecuteScalar() is not null;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.IsFavourite", ex);
            return false;
        }
    }

    public static void SetFavourite(string accountId, string folder, uint uid, bool favourite)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = favourite
                ? "INSERT OR IGNORE INTO Favourites VALUES (@a, @f, @u)"
                : "DELETE FROM Favourites WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.ExecuteNonQuery();
            FavouritesChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SetFavourite", ex);
        }
    }

    public static List<MessageRow> LoadFavourites(int limit = 500)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT {FullRowColumns} FROM Favourites v " +
                "JOIN Summaries s ON s.AccountId=v.AccountId AND s.Folder=v.Folder AND s.Uid=v.Uid " +
                "ORDER BY s.DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadFullRow(reader));
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.LoadFavourites", ex);
            return new List<MessageRow>();
        }
    }

    // ----- follow-up flags -----

    public static event EventHandler? FollowsChanged;

    public sealed record FollowInfo(long DueTicks, string EventId, bool Done);

    public static FollowInfo? FollowFor(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DueTicks, EventId, Done FROM Follows WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new FollowInfo(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0)
                : null;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.FollowFor", ex);
            return null;
        }
    }

    public static void SetFollow(string accountId, string folder, uint uid, long dueTicks, string eventId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Follows VALUES (@a, @f, @u, @d, @e, 0)";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.Parameters.AddWithValue("@d", dueTicks);
            cmd.Parameters.AddWithValue("@e", eventId);
            cmd.ExecuteNonQuery();
            FollowsChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SetFollow", ex);
        }
    }

    public static void CompleteFollow(string accountId, string folder, uint uid, bool done = true)
    {
        RunFollowUpdate("UPDATE Follows SET Done=@done WHERE AccountId=@a AND Folder=@f AND Uid=@u",
            accountId, folder, uid, done);
    }

    public static void RemoveFollow(string accountId, string folder, uint uid)
    {
        RunFollowUpdate("DELETE FROM Follows WHERE AccountId=@a AND Folder=@f AND Uid=@u",
            accountId, folder, uid, false);
    }

    private static void RunFollowUpdate(string sql, string accountId, string folder, uint uid, bool done)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            if (sql.Contains("@done"))
            {
                cmd.Parameters.AddWithValue("@done", done ? 1 : 0);
            }

            cmd.ExecuteNonQuery();
            FollowsChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.RunFollowUpdate", ex);
        }
    }

    public static HashSet<string> FollowKeys()
    {
        var set = new HashSet<string>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT AccountId, Folder, Uid FROM Follows WHERE Done = 0";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                set.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt64(2)}");
            }
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.FollowKeys", ex);
        }

        return set;
    }

    public static List<MessageRow> LoadFollowUps(int limit = 500)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT {FullRowColumns} FROM Follows w " +
                "JOIN Summaries s ON s.AccountId=w.AccountId AND s.Folder=w.Folder AND s.Uid=w.Uid " +
                "WHERE w.Done = 0 ORDER BY w.DueTicks LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadFullRow(reader));
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.LoadFollowUps", ex);
            return new List<MessageRow>();
        }
    }

    /// Distinct sender address + name pairs seen across all cached summaries (for recipient
    /// auto-complete).
    public static List<(string Address, string Name)> KnownAddresses()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT FromAddr, FromName FROM Summaries WHERE FromAddr IS NOT NULL AND FromAddr <> ''";

            var list = new List<(string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }

            return list;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.KnownAddresses", ex);
            return new List<(string, string)>();
        }
    }

    // ----- AI summaries -----

    public static string? AiSummaryFor(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Summary FROM AiSummaries WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.AiSummaryFor", ex);
            return null;
        }
    }

    public static void SaveAiSummary(string accountId, string folder, uint uid, string summary,
        long eventTicks = 0, string eventTitle = "")
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO AiSummaries VALUES (@a, @f, @u, @s, @et, @etitle)";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.Parameters.AddWithValue("@s", summary);
            cmd.Parameters.AddWithValue("@et", eventTicks);
            cmd.Parameters.AddWithValue("@etitle", eventTitle);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SaveAiSummary", ex);
        }
    }

    public sealed record BriefItem(DateTimeOffset Date, string From, string Subject, bool IsRead, int Priority, string? Summary);

    /// Recent messages across all accounts for the daily/weekly briefings, with their AI summary
    /// (first line) when one has been generated.
    public static List<BriefItem> RecentForBriefing(int days, int limit = 120)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.DateTicks, s.FromName, s.Subject, s.IsRead, s.Priority, a.Summary " +
                "FROM Summaries s LEFT JOIN AiSummaries a " +
                "ON a.AccountId=s.AccountId AND a.Folder=s.Folder AND a.Uid=s.Uid " +
                "WHERE s.DateTicks >= @cutoff ORDER BY s.DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.Now.AddDays(-days).UtcTicks);
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<BriefItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new BriefItem(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    (int)reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.RecentForBriefing", ex);
            return new List<BriefItem>();
        }
    }

    public static List<string> AiRepliesFor(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Json FROM AiReplies WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            return cmd.ExecuteScalar() is string json
                ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>()
                : new List<string>();
        }
        catch (Exception ex) when (ex is SqliteException or System.Text.Json.JsonException)
        {
            LoggingService.Warn("MessageCache.AiRepliesFor", ex);
            return new List<string>();
        }
    }

    public static void SaveAiReplies(string accountId, string folder, uint uid, IReadOnlyList<string> replies)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO AiReplies VALUES (@a, @f, @u, @j)";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.Parameters.AddWithValue("@j", System.Text.Json.JsonSerializer.Serialize(replies));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.SaveAiReplies", ex);
        }
    }

    /// (eventDateTicks, eventTitle) the model extracted for this message, or (0, "").
    public static (long Ticks, string Title) AiEventFor(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EventTicks, EventTitle FROM AiSummaries WHERE AccountId=@a AND Folder=@f AND Uid=@u";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : (0L, string.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.AiEventFor", ex);
            return (0L, string.Empty);
        }
    }

    // ----- tags -----

    public static event EventHandler? TagsChanged;

    public static string NormaliseTag(string tag)
    {
        tag = tag.Trim().TrimStart('#').Trim().ToLowerInvariant();
        tag = Regex.Replace(tag, @"\s+", "-");
        return Regex.Replace(tag, @"[^a-z0-9\-_./]", string.Empty);
    }

    public static List<string> TagsFor(string accountId, string folder, uint uid)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Tag FROM Tags WHERE AccountId=@a AND Folder=@f AND Uid=@u ORDER BY Tag";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.TagsFor", ex);
            return new List<string>();
        }
    }

    public static List<string> AllTags()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Tag FROM Tags ORDER BY Tag";

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.AllTags", ex);
            return new List<string>();
        }
    }

    public static void AddTag(string accountId, string folder, uint uid, string tag)
    {
        tag = NormaliseTag(tag);
        if (tag.Length == 0)
        {
            return;
        }

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Tags VALUES (@a, @f, @u, @t)";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.Parameters.AddWithValue("@t", tag);
            cmd.ExecuteNonQuery();
            TagsChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.AddTag", ex);
        }
    }

    public static void RemoveTag(string accountId, string folder, uint uid, string tag)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Tags WHERE AccountId=@a AND Folder=@f AND Uid=@u AND Tag=@t";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.Parameters.AddWithValue("@u", (long)uid);
            cmd.Parameters.AddWithValue("@t", NormaliseTag(tag));
            cmd.ExecuteNonQuery();
            TagsChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.RemoveTag", ex);
        }
    }

    public static List<MessageRow> MessagesWithTag(string tag, int limit = 500)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.AccountId, s.Folder, s.Uid, s.FromName, s.FromAddr, s.Subject, s.Preview, " +
                "s.DateTicks, s.HasAttachments, s.IsRead " +
                "FROM Tags t JOIN Summaries s ON s.AccountId=t.AccountId AND s.Folder=t.Folder AND s.Uid=t.Uid " +
                "WHERE t.Tag=@t ORDER BY s.DateTicks DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@t", NormaliseTag(tag));
            cmd.Parameters.AddWithValue("@lim", limit);

            var rows = new List<MessageRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MessageRow
                {
                    AccountId = reader.GetString(0),
                    Folder = reader.GetString(1),
                    Uid = (uint)reader.GetInt64(2),
                    From = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    FromAddress = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Subject = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    Preview = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    Date = new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
                    HasAttachments = reader.GetInt64(8) != 0,
                    IsRead = reader.GetInt64(9) != 0,
                });
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            LoggingService.Warn("MessageCache.MessagesWithTag", ex);
            return new List<MessageRow>();
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
                "SELECT Uid, FromName, FromAddr, Subject, Preview, DateTicks, HasAttachments, IsRead, Folder, Priority " +
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
                    Priority = (int)reader.GetInt64(9),
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
