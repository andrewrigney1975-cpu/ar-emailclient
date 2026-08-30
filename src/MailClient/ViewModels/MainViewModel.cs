using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Dispatching;

namespace MailClient.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource _listCts = new();
    private CancellationTokenSource _bodyCts = new();
    private List<MessageRow> _rows = new();

    public MainViewModel(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// Date-grouped / threaded tree shown in the message list.
    public ObservableCollection<MailListNode> ListNodes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    public partial bool AccountsLoaded { get; set; }

    public bool HasAccounts => AccountStore.All.Count > 0;

    [ObservableProperty]
    public partial string FolderTitle { get; set; } = "No folder selected";

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial MailMessageContent? CurrentMessage { get; set; }

    public bool HasMessage => CurrentMessage is not null;

    public MailAccount? CurrentAccount { get; private set; }
    public string CurrentFolder { get; private set; } = string.Empty;
    public MessageRow? CurrentOpenRow { get; private set; }

    /// A cross-account smart view (e.g. "Unread Mail") rather than a real IMAP folder.
    public string? SmartView { get; private set; }

    private MailAccount? AccountFor(MessageRow row) =>
        CurrentAccount ?? AccountStore.Find(row.AccountId);

    /// Loads a folder: cached rows first (instant), then the live IMAP fetch.
    /// <param name="quiet">Background poll - keep the existing list visible and don't show a spinner.</param>
    public async Task OpenFolderAsync(MailAccount account, string folderFullName, string title, bool quiet = false)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();
        var ct = _listCts.Token;

        CurrentAccount = account;
        CurrentFolder = folderFullName;
        SmartView = null;
        FolderTitle = title;
        CurrentMessage = null;
        CurrentOpenRow = null;

        AppSettings.Update(s =>
        {
            s.LastAccountId = account.Id;
            s.LastFolder = folderFullName;
            s.LastFolderTitle = title;
        });

        if (!quiet)
        {
            _rows = MessageCache.Load(account.Id, folderFullName);
            BuildListNodes();
        }

        IsBusy = !quiet;
        StatusText = quiet ? StatusText : "Syncing...";

        try
        {
            var live = await Task.Run(() => MailService.GetSummariesAsync(account, folderFullName, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            MessageCache.Replace(account.Id, folderFullName, live);

            var newCount = live.Count - _rows.Count;
            _dispatcher.TryEnqueue(() =>
            {
                _rows = live;
                BuildListNodes();
                StatusText = quiet && newCount > 0 ? $"{newCount} new message(s)"
                    : quiet ? StatusText
                    : $"{live.Count} message(s)";
                IsBusy = false;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.OpenFolderAsync", ex);
            _dispatcher.TryEnqueue(() =>
            {
                StatusText = "Couldn't sync: " + ex.Message;
                IsBusy = false;
            });
        }
    }

    public async Task OpenMessageAsync(MessageRow row, bool allowRemoteContent = false)
    {
        if (AccountFor(row) is null)
        {
            return;
        }

        _bodyCts.Cancel();
        _bodyCts = new CancellationTokenSource();
        var ct = _bodyCts.Token;

        CurrentMessage = null;
        CurrentOpenRow = row;
        StatusText = "Opening...";

        try
        {
            var account = AccountFor(row);
            if (account is null)
            {
                return;
            }

            var content = await Task.Run(
                () => MailService.GetMessageAsync(account, row.Folder, row.Uid, allowRemoteContent, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            CurrentMessage = content;
            StatusText = string.Empty;

            if (!row.IsRead)
            {
                row.IsRead = true;
                MessageCache.SetRead(account.Id, row.Folder, row.Uid, true);
                _ = Task.Run(() => MailService.MarkReadAsync(account, row.Folder, row.Uid, true, CancellationToken.None));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.OpenMessageAsync", ex);
            _dispatcher.TryEnqueue(() => StatusText = "Couldn't open message: " + ex.Message);
        }
    }

    public async Task DeleteAsync(MessageRow row)
    {
        if (AccountFor(row) is not { } account)
        {
            return;
        }

        _rows.RemoveAll(r => r.AccountId == row.AccountId && r.Folder == row.Folder && r.Uid == row.Uid);
        BuildListNodes();
        if (ReferenceEquals(CurrentOpenRow, row))
        {
            CurrentMessage = null;
            CurrentOpenRow = null;
        }

        try
        {
            await Task.Run(() => MailService.DeleteAsync(account, row.Folder, row.Uid, CancellationToken.None));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.DeleteAsync", ex);
            StatusText = "Delete failed: " + ex.Message;
        }
    }

    /// Marks a single message read / unread everywhere (row, cache, server).
    public void SetRead(MessageRow row, bool read)
    {
        if (row.IsRead == read || AccountFor(row) is not { } account)
        {
            return;
        }

        row.IsRead = read;
        MessageCache.SetRead(account.Id, row.Folder, row.Uid, read);
        _ = Task.Run(() => MailService.MarkReadAsync(account, row.Folder, row.Uid, read, CancellationToken.None));

        if (SmartView == "unread")
        {
            _rows.RemoveAll(r => r.IsRead);
            BuildListNodes();
        }
    }

    /// Marks every message currently in the list as read.
    public async Task MarkAllReadAsync()
    {
        var targets = _rows.Where(r => !r.IsRead).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var row in targets)
        {
            row.IsRead = true;
            if (AccountFor(row) is { } acc)
            {
                MessageCache.SetRead(acc.Id, row.Folder, row.Uid, true);
            }
        }

        var groups = targets
            .Select(r => (Account: AccountFor(r), r.Folder, r.Uid))
            .Where(g => g.Account is not null)
            .GroupBy(g => (g.Account!.Id, g.Folder));

        foreach (var g in groups)
        {
            var account = g.First().Account!;
            var folder = g.Key.Folder;
            var uids = g.Select(x => x.Uid).ToList();
            try
            {
                await Task.Run(() => MailService.MarkReadBulkAsync(account, folder, uids, true, CancellationToken.None));
            }
            catch (Exception ex)
            {
                LoggingService.Warn("MainViewModel.MarkAllReadAsync", ex);
            }
        }

        if (SmartView == "unread")
        {
            _rows.Clear();
            BuildListNodes();
        }

        StatusText = "Marked all as read";
    }

    /// Cross-account view of every unread cached message.
    public async Task ShowUnreadAsync()
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = "unread";
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Loading unread…";

        var rows = await Task.Run(() => MessageCache.LoadUnread());

        _rows = rows;
        BuildListNodes();
        FolderTitle = "Unread Mail";
        StatusText = $"{rows.Count} unread";
        IsBusy = false;
    }

    /// Runs a cached-summary search for an account and shows the hits in the message list.
    public async Task SearchAsync(MailAccount account, string query)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = account;
        SmartView = null;
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Searching...";

        var hits = await Task.Run(() => MessageCache.Search(account.Id, query));

        _rows = hits;
        BuildListNodes();
        FolderTitle = $"Search: “{query}”";
        StatusText = $"{hits.Count} result(s)";
        IsBusy = false;
    }

    /// Clears the current folder/message view, e.g. after its account is removed.
    public void ClearView()
    {
        _listCts.Cancel();
        _bodyCts.Cancel();
        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = null;
        CurrentOpenRow = null;
        _rows = new List<MessageRow>();
        ListNodes.Clear();
        CurrentMessage = null;
        FolderTitle = "No folder selected";
        StatusText = string.Empty;
        IsBusy = false;
    }

    public Task RefreshAsync(bool quiet = false) =>
        SmartView == "unread" ? ShowUnreadAsync()
        : CurrentAccount is { } acc && CurrentFolder.Length > 0
            ? OpenFolderAsync(acc, CurrentFolder, FolderTitle, quiet)
            : Task.CompletedTask;

    // ----- list grouping -----

    private void BuildListNodes()
    {
        ListNodes.Clear();

        var buckets = _rows
            .GroupBy(r => DateBucket(r.Date))
            .OrderBy(g => g.Key.Sort);

        foreach (var bucket in buckets)
        {
            var groupNode = new MailListNode
            {
                Kind = MailListKind.DateGroup,
                Header = bucket.Key.Name,
                MessageCount = bucket.Count(),
                IsExpanded = true,
            };

            var threads = bucket
                .GroupBy(NormaliseSubject)
                .Select(g => g.OrderByDescending(r => r.Date).ToList())
                .OrderByDescending(list => list[0].Date);

            foreach (var thread in threads)
            {
                if (thread.Count == 1)
                {
                    groupNode.Children.Add(MessageNode(thread[0]));
                    continue;
                }

                var threadNode = new MailListNode
                {
                    Kind = MailListKind.Thread,
                    Header = string.IsNullOrWhiteSpace(thread[0].Subject) ? "(no subject)" : thread[0].Subject,
                    MessageCount = thread.Count,
                    IsExpanded = false,
                };

                foreach (var row in thread)
                {
                    threadNode.Children.Add(MessageNode(row));
                }

                groupNode.Children.Add(threadNode);
            }

            ListNodes.Add(groupNode);
        }
    }

    private static MailListNode MessageNode(MessageRow row) =>
        new() { Kind = MailListKind.Message, Row = row };

    private static string NormaliseSubject(MessageRow row)
    {
        var s = (row.Subject ?? string.Empty).Trim();
        s = Regex.Replace(s, @"^(?:\s*(?:re|fw|fwd|aw|sv)\s*:\s*)+", string.Empty, RegexOptions.IgnoreCase);
        return s.ToLowerInvariant();
    }

    private static (int Sort, string Name) DateBucket(DateTimeOffset when)
    {
        var day = when.LocalDateTime.Date;
        var today = DateTime.Today;

        if (day == today) return (0, "Today");
        if (day == today.AddDays(-1)) return (1, "Yesterday");

        var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
        if (day >= startOfWeek) return (2, "This Week");
        if (day >= startOfWeek.AddDays(-7)) return (3, "Last Week");

        var startOfMonth = new DateTime(today.Year, today.Month, 1);
        if (day >= startOfMonth) return (4, "This Month");
        if (day >= startOfMonth.AddMonths(-1)) return (5, "Last Month");

        return (6, "Older");
    }
}
