using MailClient.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailClient.Services;

/// Server push via IMAP IDLE. One dedicated connection per account watches that account's INBOX
/// and raises <see cref="NewMessages"/> when the server reports new mail. IDLE monopolises its
/// connection, so this is deliberately separate from the pooled connection in <see cref="MailService"/>.
///
/// Accounts whose server lacks the IDLE capability (or where IDLE keeps failing) are reported via
/// <see cref="AllAccountsPushing"/> so the caller can keep a faster poll running as the fallback.
public static class ImapIdleService
{
    private sealed class Watcher
    {
        public required MailAccount Account { get; init; }
        public CancellationTokenSource Stop { get; set; } = new();
        public Task? Loop { get; set; }
        public bool Pushing { get; set; }
    }

    private static readonly Dictionary<string, Watcher> Watchers = new();
    private static readonly object Lock = new();

    /// Raised (on a background thread) when an account's INBOX gains messages. Args: accountId, folderFullName.
    public static event Action<string, string>? NewMessages;

    /// Raised when the set of accounts successfully using IDLE changes.
    public static event Action? PushStateChanged;

    /// True only when every configured account has a live IDLE connection. While false the caller
    /// should keep polling at the normal (fast) interval.
    public static bool AllAccountsPushing
    {
        get
        {
            lock (Lock)
            {
                return Watchers.Count > 0 && Watchers.Values.All(w => w.Pushing);
            }
        }
    }

    public static void Start(IEnumerable<MailAccount> accounts)
    {
        lock (Lock)
        {
            var wanted = accounts.ToDictionary(a => a.Id);

            foreach (var id in Watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                StopWatcher(id);
            }

            foreach (var (id, account) in wanted)
            {
                if (Watchers.ContainsKey(id))
                {
                    continue;
                }

                var watcher = new Watcher { Account = account };
                Watchers[id] = watcher;
                watcher.Loop = Task.Run(() => RunAsync(watcher, watcher.Stop.Token));
            }
        }
    }

    public static void StopAll()
    {
        lock (Lock)
        {
            foreach (var id in Watchers.Keys.ToList())
            {
                StopWatcher(id);
            }
        }
    }

    private static void StopWatcher(string accountId)
    {
        if (Watchers.Remove(accountId, out var watcher))
        {
            try { watcher.Stop.Cancel(); } catch { /* best effort */ }
        }
    }

    private static async Task RunAsync(Watcher watcher, CancellationToken stop)
    {
        var account = watcher.Account;
        var backoff = TimeSpan.FromSeconds(5);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = new ImapClient { Timeout = 60_000 };

                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(30));
                    await client.ConnectAsync(account.ImapHost, account.ImapPort,
                        account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
                        handshake.Token);
                    await client.AuthenticateAsync(account.Username, AccountStore.PasswordOf(account), handshake.Token);
                }

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    LoggingService.Info("ImapIdleService", $"{account.Email}: server has no IDLE, falling back to polling");
                    SetPushing(watcher, false);
                    return;
                }

                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, stop);
                var lastCount = inbox.Count;

                void OnCountChanged(object? s, EventArgs e)
                {
                    if (inbox.Count > lastCount)
                    {
                        LoggingService.Info("ImapIdleService", $"{account.Email}: {inbox.Count - lastCount} new in INBOX (push)");
                        NewMessages?.Invoke(account.Id, inbox.FullName);
                    }

                    lastCount = inbox.Count;
                }

                inbox.CountChanged += OnCountChanged;
                SetPushing(watcher, true);
                backoff = TimeSpan.FromSeconds(5);

                try
                {
                    while (!stop.IsCancellationRequested && client.IsConnected)
                    {
                        // RFC 2177: renew IDLE well within the 29-minute server limit.
                        using var renew = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                        try
                        {
                            await client.IdleAsync(renew.Token, stop);
                        }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                        {
                            // renew timer fired - loop and start a fresh IDLE
                        }
                    }
                }
                finally
                {
                    inbox.CountChanged -= OnCountChanged;
                }

                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"ImapIdleService: {account.Email} idle loop, retrying in {backoff.TotalSeconds:0}s", ex);
                SetPushing(watcher, false);
                try { await Task.Delay(backoff, stop); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
            }
        }

        SetPushing(watcher, false);
    }

    private static void SetPushing(Watcher watcher, bool pushing)
    {
        bool changed;
        lock (Lock)
        {
            changed = watcher.Pushing != pushing;
            watcher.Pushing = pushing;
        }

        if (changed)
        {
            try { PushStateChanged?.Invoke(); } catch { /* best effort */ }
        }
    }
}
