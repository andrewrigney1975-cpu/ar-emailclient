using System.Text.RegularExpressions;
using MailClient.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailClient.Services;

/// Thin MailKit wrapper: one long-lived IMAP connection per account (operations serialised with a
/// semaphore and reconnected on demand), plus fire-and-forget SMTP sends.
public static class MailService
{
    private const int SummaryFetchCount = 80;

    private static readonly MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText;

    private static readonly Dictionary<string, ImapConnection> Connections = new();
    private static readonly object ConnLock = new();

    private static ImapConnection ConnectionFor(MailAccount account)
    {
        lock (ConnLock)
        {
            if (!Connections.TryGetValue(account.Id, out var conn))
            {
                Connections[account.Id] = conn = new ImapConnection(account);
            }

            return conn;
        }
    }

    public static void Disconnect(string accountId)
    {
        lock (ConnLock)
        {
            if (Connections.Remove(accountId, out var conn))
            {
                conn.Dispose();
            }
        }
    }

    public static void DisconnectAll()
    {
        lock (ConnLock)
        {
            foreach (var conn in Connections.Values)
            {
                conn.Dispose();
            }

            Connections.Clear();
        }
    }

    // ----- folders -----

    public sealed record FolderInfo(string FullName, string Name, string[] Path, int Unread, string Role = "");

    /// A SPECIAL-USE role ("inbox", "sent", "drafts", "trash", "junk", "archive") or "".
    private static string RoleOf(IMailFolder folder)
    {
        var a = folder.Attributes;
        if ((a & FolderAttributes.Sent) != 0) return "sent";
        if ((a & FolderAttributes.Drafts) != 0) return "drafts";
        if ((a & FolderAttributes.Trash) != 0) return "trash";
        if ((a & FolderAttributes.Junk) != 0) return "junk";
        if ((a & (FolderAttributes.Archive | FolderAttributes.All)) != 0) return "archive";

        // Fall back to the folder name for servers without SPECIAL-USE.
        return folder.Name.ToLowerInvariant() switch
        {
            "inbox" => "inbox",
            "sent" or "sent mail" or "sent items" or "sent messages" => "sent",
            "drafts" => "drafts",
            "trash" or "deleted" or "deleted items" or "bin" => "trash",
            "junk" or "spam" or "junk email" => "junk",
            "archive" or "all mail" => "archive",
            _ => string.Empty,
        };
    }

    public static async Task<List<FolderInfo>> GetFoldersAsync(MailAccount account, CancellationToken ct)
    {
        LoggingService.Info("MailService.GetFoldersAsync", $"listing folders for {account.Email}");
        var conn = ConnectionFor(account);
        var folders = await conn.RunAsync(async client =>
        {
            var result = new List<FolderInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ns in client.PersonalNamespaces)
            {
                var root = client.GetFolder(ns);
                await AddFolderTreeAsync(root, result, seen, ct);
            }

            if (seen.Add(client.Inbox.FullName))
            {
                await client.Inbox.StatusAsync(StatusItems.Unread, ct);
                result.Insert(0, new FolderInfo(client.Inbox.FullName, "Inbox", new[] { "Inbox" }, client.Inbox.Unread, "inbox"));
            }

            return result;
        }, ct);

        LoggingService.Info("MailService.GetFoldersAsync", $"got {folders.Count} folder(s) for {account.Email}");
        return folders;
    }

    public static async Task CreateFolderAsync(MailAccount account, string parentFullName, string name, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var parent = string.IsNullOrEmpty(parentFullName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(parentFullName, ct);
            await parent.CreateAsync(name.Trim(), isMessageFolder: true, ct);
            return true;
        }, ct);
    }

    public static async Task RenameFolderAsync(MailAccount account, string folderFullName, string newName, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderFullName, ct);
            await folder.RenameAsync(folder.ParentFolder, newName.Trim(), ct);
            return true;
        }, ct);
    }

    public static async Task MoveFolderAsync(MailAccount account, string folderFullName, string newParentFullName, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderFullName, ct);
            var newParent = string.IsNullOrEmpty(newParentFullName)
                ? client.GetFolder(client.PersonalNamespaces[0])
                : await client.GetFolderAsync(newParentFullName, ct);
            await folder.RenameAsync(newParent, folder.Name, ct);
            return true;
        }, ct);
    }

    public static async Task DeleteFolderAsync(MailAccount account, string folderFullName, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await client.GetFolderAsync(folderFullName, ct);
            await folder.DeleteAsync(ct);
            return true;
        }, ct);
    }

    private static async Task AddFolderTreeAsync(IMailFolder parent, List<FolderInfo> into, HashSet<string> seen, CancellationToken ct)
    {
        IList<IMailFolder> children;
        try
        {
            children = await parent.GetSubfoldersAsync(StatusItems.Unread, false, ct);
        }
        catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException)
        {
            return;
        }

        foreach (var folder in children)
        {
            if ((folder.Attributes & FolderAttributes.NonExistent) != 0)
            {
                continue;
            }

            if ((folder.Attributes & FolderAttributes.NoSelect) == 0 && seen.Add(folder.FullName))
            {
                var sep = folder.DirectorySeparator == '\0' ? '/' : folder.DirectorySeparator;
                var path = folder.FullName.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                var name = folder.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "Inbox" : folder.Name;
                into.Add(new FolderInfo(folder.FullName, name,
                    path.Length == 0 ? new[] { name } : path, folder.Unread, RoleOf(folder)));
            }

            await AddFolderTreeAsync(folder, into, seen, ct);
        }
    }

    /// 2 = high, 1 = normal, 0 = low. Reads Importance, then X-Priority, then Priority.
    private static int PriorityFromHeaders(HeaderList headers)
    {
        var importance = headers["Importance"]?.Trim().ToLowerInvariant();
        if (importance == "high") return 2;
        if (importance == "low") return 0;

        var xPriority = headers["X-Priority"]?.TrimStart();
        if (xPriority is { Length: > 0 })
        {
            if (xPriority[0] is '1' or '2') return 2;
            if (xPriority[0] is '4' or '5') return 0;
        }

        var priority = headers["Priority"]?.Trim().ToLowerInvariant();
        if (priority == "urgent") return 2;
        if (priority == "non-urgent") return 0;

        return 1;
    }

    // ----- message list -----

    public static async Task<List<MessageRow>> GetSummariesAsync(MailAccount account, string folderFullName, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        return await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadOnly, ct);
            var count = folder.Count;
            if (count == 0)
            {
                return new List<MessageRow>();
            }

            var start = Math.Max(0, count - SummaryFetchCount);
            var summaries = await folder.FetchAsync(start, -1, SummaryItems,
                new[] { "Importance", "Priority", "X-Priority" }, ct);

            return summaries
                .OrderByDescending(s => s.Date)
                .Select(s => new MessageRow
                {
                    AccountId = account.Id,
                    Folder = folderFullName,
                    Uid = s.UniqueId.Id,
                    From = s.Envelope?.From.Mailboxes.FirstOrDefault()?.Name is { Length: > 0 } n
                        ? n
                        : s.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? "(unknown)",
                    FromAddress = s.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                    Subject = s.Envelope?.Subject ?? string.Empty,
                    Preview = s.PreviewText ?? string.Empty,
                    Date = s.Date,
                    HasAttachments = s.Attachments.Any(),
                    IsRead = s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                    Priority = PriorityFromHeaders(s.Headers),
                })
                .ToList();
        }, ct);
    }

    // ----- single message -----

    public static async Task<MailMessageContent> GetMessageAsync(MailAccount account, string folderFullName, uint uid, bool allowRemoteContent, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        return await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadOnly, ct);
            var message = await folder.GetMessageAsync(new UniqueId(uid), ct);

            var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
            var allowRemote = allowRemoteContent || RemoteContentStore.IsAllowed(fromAddress);

            var (html, hadRemote) = message.HtmlBody is { } rawHtml
                ? NeutraliseRemoteContent(rawHtml, allowRemote)
                : (null, false);

            var attachments = message.Attachments
                .Select((a, i) => new MailAttachmentInfo(
                    a.ContentDisposition?.FileName ?? a.ContentType.Name ?? $"attachment-{i + 1}",
                    (a as MimePart)?.Content?.Stream?.Length ?? 0,
                    i))
                .ToList();

            return new MailMessageContent
            {
                Subject = message.Subject ?? string.Empty,
                FromDisplay = string.Join(", ", message.From.Mailboxes.Select(m => m.Name is { Length: > 0 } ? $"{m.Name} <{m.Address}>" : m.Address)),
                FromAddress = fromAddress,
                ReplyToAddress = message.ReplyTo.Mailboxes.FirstOrDefault()?.Address
                    ?? fromAddress,
                ToDisplay = string.Join(", ", message.To.Mailboxes.Select(m => m.Address)),
                CcDisplay = string.Join(", ", message.Cc.Mailboxes.Select(m => m.Address)),
                Date = message.Date,
                Html = html,
                PlainText = message.TextBody,
                HadRemoteContent = hadRemote,
                RemoteContentAllowed = allowRemote,
                Attachments = attachments,
                Priority = message.Importance == MessageImportance.High || message.Priority == MessagePriority.Urgent
                    || message.XPriority is XMessagePriority.Highest or XMessagePriority.High ? 2
                    : message.Importance == MessageImportance.Low || message.Priority == MessagePriority.NonUrgent
                    || message.XPriority is XMessagePriority.Low or XMessagePriority.Lowest ? 0
                    : 1,
                MessageId = message.MessageId ?? string.Empty,
                References = string.Join(" ", message.References),
            };
        }, ct);
    }

    /// Downloads one attachment (by its index in MailMessageContent.Attachments) as raw bytes.
    public static async Task<(string FileName, byte[] Data)> GetAttachmentAsync(
        MailAccount account, string folderFullName, uint uid, int index, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        return await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadOnly, ct);
            var message = await folder.GetMessageAsync(new UniqueId(uid), ct);

            var attachment = message.Attachments.ElementAtOrDefault(index)
                ?? throw new InvalidOperationException("Attachment not found.");

            using var ms = new MemoryStream();
            if (attachment is MessagePart messagePart)
            {
                await messagePart.Message.WriteToAsync(ms, ct);
            }
            else if (attachment is MimePart mimePart)
            {
                await mimePart.Content.DecodeToAsync(ms, ct);
            }

            var name = attachment.ContentDisposition?.FileName
                ?? attachment.ContentType.Name
                ?? $"attachment-{index + 1}";

            return (SanitiseFileName(name), ms.ToArray());
        }, ct);
    }

    private static string SanitiseFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }

    public static async Task MarkReadAsync(MailAccount account, string folderFullName, uint uid, bool read, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadWrite, ct);
            if (read)
            {
                await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, ct);
            }
            else
            {
                await folder.RemoveFlagsAsync(new UniqueId(uid), MessageFlags.Seen, true, ct);
            }

            return true;
        }, ct);
    }

    public static async Task MarkReadBulkAsync(
        MailAccount account, string folderFullName, IReadOnlyList<uint> uids, bool read, CancellationToken ct)
    {
        if (uids.Count == 0)
        {
            return;
        }

        var ids = uids.Select(u => new UniqueId(u)).ToList();
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadWrite, ct);
            if (read)
            {
                await folder.AddFlagsAsync(ids, MessageFlags.Seen, true, ct);
            }
            else
            {
                await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, true, ct);
            }

            return true;
        }, ct);
    }

    public static async Task SetFlaggedAsync(MailAccount account, string folderFullName, uint uid, bool flagged, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadWrite, ct);
            if (flagged)
            {
                await folder.AddFlagsAsync(new UniqueId(uid), MessageFlags.Flagged, true, ct);
            }
            else
            {
                await folder.RemoveFlagsAsync(new UniqueId(uid), MessageFlags.Flagged, true, ct);
            }

            return true;
        }, ct);
    }

    public static async Task MoveAsync(MailAccount account, string fromFolder, uint uid, string toFolder, CancellationToken ct)
    {
        if (fromFolder.Equals(toFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var source = await OpenAsync(client, fromFolder, FolderAccess.ReadWrite, ct);
            var destination = await client.GetFolderAsync(toFolder, ct);
            await source.MoveToAsync(new UniqueId(uid), destination, ct);
            return true;
        }, ct);
    }

    public static async Task DeleteAsync(MailAccount account, string folderFullName, uint uid, CancellationToken ct)
    {
        var conn = ConnectionFor(account);
        await conn.RunAsync(async client =>
        {
            var folder = await OpenAsync(client, folderFullName, FolderAccess.ReadWrite, ct);
            var trash = client.GetFolder(SpecialFolder.Trash);
            var id = new UniqueId(uid);

            if (trash is not null && !trash.FullName.Equals(folderFullName, StringComparison.OrdinalIgnoreCase))
            {
                await folder.MoveToAsync(id, trash, ct);
            }
            else
            {
                await folder.AddFlagsAsync(id, MessageFlags.Deleted, true, ct);
                await folder.ExpungeAsync(ct);
            }

            return true;
        }, ct);
    }

    // ----- send -----

    public static async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken ct)
    {
        using var smtp = new SmtpClient();
        var options = account.SmtpUseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTlsWhenAvailable;
        await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, options, ct);
        await smtp.AuthenticateAsync(account.Username, AccountStore.PasswordOf(account), ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    /// A minimal connect+auth used by the Add Account dialog to validate credentials before saving.
    public static async Task VerifyAsync(MailAccount account, string plainPassword, CancellationToken ct)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(account.ImapHost, account.ImapPort,
            account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
        await imap.AuthenticateAsync(account.Username, plainPassword, ct);
        await imap.DisconnectAsync(true, ct);

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort,
            account.SmtpUseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTlsWhenAvailable, ct);
        await smtp.AuthenticateAsync(account.Username, plainPassword, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    // ----- helpers -----

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string fullName, FolderAccess access, CancellationToken ct)
    {
        var folder = fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(fullName, ct);

        if (folder.Access != access || !folder.IsOpen)
        {
            await folder.OpenAsync(access, ct);
        }

        return folder;
    }

    /// Blocks remote images/CSS (tracking pixels) unless the user has opted in for this message.
    private static (string Html, bool HadRemote) NeutraliseRemoteContent(string html, bool allow)
    {
        if (allow)
        {
            return (html, Regex.IsMatch(html, @"(?i)(src|background)\s*=\s*[""']?https?://"));
        }

        var hadRemote = false;
        var neutralised = Regex.Replace(html, @"(?i)(<img\b[^>]*?\bsrc\s*=\s*)([""']?)https?://[^""'\s>]+\2",
            m => { hadRemote = true; return m.Groups[1].Value + "\"\""; });

        neutralised = Regex.Replace(neutralised, @"(?i)(background\s*=\s*)([""']?)https?://[^""'\s>]+\2",
            m => { hadRemote = true; return m.Groups[1].Value + "\"\""; });

        neutralised = Regex.Replace(neutralised, @"(?i)url\(\s*[""']?https?://[^)]+\)",
            m => { hadRemote = true; return "none"; });

        return (neutralised, hadRemote);
    }

    private sealed class ImapConnection : IDisposable
    {
        private readonly MailAccount _account;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ImapClient? _client;

        public ImapConnection(MailAccount account) => _account = account;

        public async Task<T> RunAsync<T>(Func<ImapClient, Task<T>> op, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var client = await EnsureConnectedAsync(ct);
                try
                {
                    return await op(client);
                }
                catch (Exception ex) when (ex is ImapProtocolException or IOException or ServiceNotConnectedException)
                {
                    // Stale connection - drop it and retry once from scratch.
                    LoggingService.Warn("ImapConnection: reconnecting", ex);
                    _client?.Dispose();
                    _client = null;
                    return await op(await EnsureConnectedAsync(ct));
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client is { IsConnected: true, IsAuthenticated: true })
            {
                return _client;
            }

            _client?.Dispose();
            _client = new ImapClient { Timeout = 60_000 };

            // ImapClient.Timeout does not bound the initial connect / TLS / auth handshake, so guard
            // it explicitly - otherwise an unresponsive server hangs the caller forever.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await _client.ConnectAsync(_account.ImapHost, _account.ImapPort,
                    _account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
                    handshakeCts.Token);
                await _client.AuthenticateAsync(_account.Username, AccountStore.PasswordOf(_account), handshakeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out connecting to {_account.ImapHost}:{_account.ImapPort}. Check the server address, port and SSL setting.");
            }

            return _client;
        }

        public void Dispose()
        {
            try { _client?.Dispose(); } catch { /* best effort */ }
            _gate.Dispose();
        }
    }
}
