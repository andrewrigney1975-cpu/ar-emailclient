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

    public MessageRow? FindRow(string folder, uint uid) =>
        _rows.FirstOrDefault(r => r.Folder == folder && r.Uid == uid);

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

            var known = _rows.Select(r => r.Uid).ToHashSet();
            var fresh = live.Where(r => !known.Contains(r.Uid)).OrderByDescending(r => r.Date).ToList();

            _dispatcher.TryEnqueue(() =>
            {
                _rows = live;
                BuildListNodes();
                StatusText = quiet && fresh.Count > 0 ? $"{fresh.Count} new message(s)"
                    : quiet ? StatusText
                    : $"{live.Count} message(s)";
                IsBusy = false;

                if (quiet && fresh.Count > 0)
                {
                    var newest = fresh[0];
                    var body = fresh.Count == 1
                        ? $"{newest.From}: {newest.SubjectDisplay}"
                        : $"{fresh.Count} new messages in {title}";
                    NotificationService.ShowNewMail("New mail", body,
                        new MailRef(account.Id, folderFullName, newest.Uid));
                }
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

    /// Moves a message to another folder of the same account (row, cache, server).
    public async Task MoveAsync(MessageRow row, string destinationFolder)
    {
        if (AccountFor(row) is not { } account ||
            row.Folder.Equals(destinationFolder, StringComparison.OrdinalIgnoreCase))
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

        MessageCache.RemoveMessage(row.AccountId, row.Folder, row.Uid);

        try
        {
            await Task.Run(() => MailService.MoveAsync(account, row.Folder, row.Uid, destinationFolder, CancellationToken.None));
            StatusText = "Moved to " + destinationFolder.Split('/', '.').Last();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.MoveAsync", ex);
            StatusText = "Move failed: " + ex.Message;
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

        StatusText = $"Marked {targets.Count} as read";
        NotificationService.Show("Mail",
            $"{targets.Count} message{(targets.Count == 1 ? "" : "s")} marked as read");
    }

    /// Cross-account view of every folder with a given SPECIAL-USE role ("inbox", "sent").
    public async Task ShowRoleAsync(string role, string title)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = role;
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Loading…";

        var rows = await Task.Run(() => MessageCache.LoadByRole(role));

        _rows = rows;
        BuildListNodes();
        FolderTitle = title;
        StatusText = $"{rows.Count} message(s)";
        IsBusy = false;
    }

    /// Cross-account view of every open follow-up, earliest due first.
    public async Task ShowFollowUpsAsync()
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = "followups";
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Loading…";

        var rows = await Task.Run(() => MessageCache.LoadFollowUps());

        _rows = rows;
        BuildListNodes();
        FolderTitle = "Follow Up";
        StatusText = $"{rows.Count} to follow up";
        IsBusy = false;
    }

    /// Cross-account view of every starred message.
    public async Task ShowFavouritesAsync()
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = "favourites";
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Loading…";

        var rows = await Task.Run(() => MessageCache.LoadFavourites());

        _rows = rows;
        BuildListNodes();
        FolderTitle = "Favourites";
        StatusText = $"{rows.Count} starred";
        IsBusy = false;
    }

    public void SetFavourite(MessageRow row, bool favourite)
    {
        if (row.IsFavourite == favourite)
        {
            return;
        }

        row.IsFavourite = favourite;
        MessageCache.SetFavourite(row.AccountId, row.Folder, row.Uid, favourite);

        if (SmartView == "favourites" && !favourite)
        {
            _rows.RemoveAll(r => !r.IsFavourite);
            BuildListNodes();
        }
    }

    /// Cross-account view of every message carrying a given tag.
    public async Task ShowTagAsync(string tag)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = null;
        CurrentFolder = string.Empty;
        SmartView = "tag:" + tag;
        CurrentMessage = null;
        CurrentOpenRow = null;
        IsBusy = true;
        StatusText = "Loading…";

        var rows = await Task.Run(() => MessageCache.MessagesWithTag(tag));

        _rows = rows;
        BuildListNodes();
        FolderTitle = "#" + tag;
        StatusText = $"{rows.Count} tagged";
        IsBusy = false;
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

    /// Called when IMAP IDLE reports new mail in a folder. Updates the cache and fires a toast
    /// even if the user is looking at a different folder/account; refreshes the list if it's current.
    public async Task HandlePushedNewMailAsync(string accountId, string folderFullName)
    {
        if (AccountStore.Find(accountId) is not { } account)
        {
            return;
        }

        try
        {
            var known = MessageCache.Load(accountId, folderFullName).Select(r => r.Uid).ToHashSet();
            var live = await Task.Run(() => MailService.GetSummariesAsync(account, folderFullName, CancellationToken.None));
            MessageCache.Replace(accountId, folderFullName, live);

            var fresh = live.Where(r => !known.Contains(r.Uid)).OrderByDescending(r => r.Date).ToList();
            var isCurrent = CurrentAccount?.Id == accountId
                && CurrentFolder.Equals(folderFullName, StringComparison.OrdinalIgnoreCase);

            _dispatcher.TryEnqueue(() =>
            {
                if (isCurrent)
                {
                    _rows = live;
                    BuildListNodes();
                }

                if (fresh.Count > 0)
                {
                    var newest = fresh[0];
                    var label = folderFullName.Split('/', '.').Last();
                    var body = fresh.Count == 1
                        ? $"{newest.From}: {newest.SubjectDisplay}"
                        : $"{fresh.Count} new messages in {label}";
                    NotificationService.ShowNewMail("New mail", body, new MailRef(accountId, folderFullName, newest.Uid));
                    StatusText = $"{fresh.Count} new message(s)";
                }
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.HandlePushedNewMailAsync", ex);
        }
    }

    public Task RefreshAsync(bool quiet = false) => SmartView switch
    {
        "unread" => ShowUnreadAsync(),
        "favourites" => ShowFavouritesAsync(),
        "followups" => ShowFollowUpsAsync(),
        "inbox" => ShowRoleAsync("inbox", FolderTitle),
        "sent" => ShowRoleAsync("sent", FolderTitle),
        { } sv when sv.StartsWith("tag:", StringComparison.Ordinal) => ShowTagAsync(sv[4..]),
        _ => CurrentAccount is { } acc && CurrentFolder.Length > 0
            ? OpenFolderAsync(acc, CurrentFolder, FolderTitle, quiet)
            : Task.CompletedTask,
    };

    // ----- list grouping -----

    // Expand/collapse state, keyed by StateKey, kept across list rebuilds (sync / poll) and,
    // for date groups, across sessions via AppSettings.
    private readonly Dictionary<string, bool> _groupExpanded = new();
    private readonly Dictionary<string, bool> _threadExpanded = new();
    private bool _groupStateLoaded;

    private void LoadGroupState()
    {
        if (_groupStateLoaded)
        {
            return;
        }

        _groupStateLoaded = true;
        foreach (var name in AppSettings.Current.CollapsedDateGroups)
        {
            _groupExpanded[name] = false;
        }
    }

    private void BuildListNodes()
    {
        LoadGroupState();
        ListNodes.Clear();

        var favourites = MessageCache.FavouriteKeys();
        var flagged = MessageCache.FollowKeys();
        foreach (var row in _rows)
        {
            var key = $"{row.AccountId}|{row.Folder}|{row.Uid}";
            row.IsFavourite = favourites.Contains(key);
            row.IsFlagged = flagged.Contains(key);
        }

        var buckets = _rows
            .GroupBy(r => DateBucket(r.Date))
            .OrderBy(g => g.Key.Sort);

        foreach (var bucket in buckets)
        {
            var groupKey = bucket.Key.Name;
            var groupNode = new MailListNode
            {
                Kind = MailListKind.DateGroup,
                Header = groupKey,
                MessageCount = bucket.Count(),
                StateKey = groupKey,
                IsExpanded = !_groupExpanded.TryGetValue(groupKey, out var ge) || ge,
            };
            groupNode.PropertyChanged += OnListNodeExpandedChanged;

            var threads = bucket
                .GroupBy(NormaliseSubject)
                .Select(g => (Subject: g.Key, Rows: g.OrderByDescending(r => r.Date).ToList()))
                .OrderByDescending(t => t.Rows[0].Date);

            foreach (var thread in threads)
            {
                if (thread.Rows.Count == 1)
                {
                    groupNode.Children.Add(MessageNode(thread.Rows[0]));
                    continue;
                }

                var threadKey = $"{groupKey}|{thread.Subject}";
                var threadNode = new MailListNode
                {
                    Kind = MailListKind.Thread,
                    Header = string.IsNullOrWhiteSpace(thread.Rows[0].Subject) ? "(no subject)" : thread.Rows[0].Subject,
                    MessageCount = thread.Rows.Count,
                    StateKey = threadKey,
                    IsExpanded = _threadExpanded.TryGetValue(threadKey, out var te) && te,
                };
                threadNode.PropertyChanged += OnListNodeExpandedChanged;

                foreach (var row in thread.Rows)
                {
                    threadNode.Children.Add(MessageNode(row));
                }

                groupNode.Children.Add(threadNode);
            }

            ListNodes.Add(groupNode);
        }
    }

    private void OnListNodeExpandedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MailListNode.IsExpanded) ||
            sender is not MailListNode node || node.StateKey.Length == 0)
        {
            return;
        }

        if (node.Kind == MailListKind.DateGroup)
        {
            _groupExpanded[node.StateKey] = node.IsExpanded;
            AppSettings.Update(s => s.CollapsedDateGroups =
                _groupExpanded.Where(kv => !kv.Value).Select(kv => kv.Key).ToList());
        }
        else if (node.Kind == MailListKind.Thread)
        {
            _threadExpanded[node.StateKey] = node.IsExpanded;
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
