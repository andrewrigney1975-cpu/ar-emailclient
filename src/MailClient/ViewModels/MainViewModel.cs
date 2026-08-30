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

    /// Loads a folder: cached rows first (instant), then the live IMAP fetch.
    public async Task OpenFolderAsync(MailAccount account, string folderFullName, string title)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();
        var ct = _listCts.Token;

        CurrentAccount = account;
        CurrentFolder = folderFullName;
        FolderTitle = title;
        CurrentMessage = null;
        CurrentOpenRow = null;

        AppSettings.Update(s =>
        {
            s.LastAccountId = account.Id;
            s.LastFolder = folderFullName;
            s.LastFolderTitle = title;
        });

        _rows = MessageCache.Load(account.Id, folderFullName);
        BuildListNodes();

        IsBusy = true;
        StatusText = "Syncing...";

        try
        {
            var live = await Task.Run(() => MailService.GetSummariesAsync(account, folderFullName, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            MessageCache.Replace(account.Id, folderFullName, live);

            _dispatcher.TryEnqueue(() =>
            {
                _rows = live;
                BuildListNodes();
                StatusText = $"{live.Count} message(s)";
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
        if (CurrentAccount is null)
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
            var content = await Task.Run(
                () => MailService.GetMessageAsync(CurrentAccount, row.Folder, row.Uid, allowRemoteContent, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            CurrentMessage = content;
            StatusText = string.Empty;

            if (!row.IsRead)
            {
                row.IsRead = true;
                MessageCache.SetRead(CurrentAccount.Id, row.Folder, row.Uid, true);
                _ = Task.Run(() => MailService.MarkReadAsync(CurrentAccount, row.Folder, row.Uid, true, CancellationToken.None));
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
        if (CurrentAccount is null)
        {
            return;
        }

        _rows.RemoveAll(r => r.Folder == row.Folder && r.Uid == row.Uid);
        BuildListNodes();
        if (ReferenceEquals(CurrentOpenRow, row))
        {
            CurrentMessage = null;
            CurrentOpenRow = null;
        }

        try
        {
            await Task.Run(() => MailService.DeleteAsync(CurrentAccount, row.Folder, row.Uid, CancellationToken.None));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel.DeleteAsync", ex);
            StatusText = "Delete failed: " + ex.Message;
        }
    }

    /// Runs a cached-summary search for an account and shows the hits in the message list.
    public async Task SearchAsync(MailAccount account, string query)
    {
        _listCts.Cancel();
        _listCts = new CancellationTokenSource();

        CurrentAccount = account;
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
        CurrentOpenRow = null;
        _rows = new List<MessageRow>();
        ListNodes.Clear();
        CurrentMessage = null;
        FolderTitle = "No folder selected";
        StatusText = string.Empty;
        IsBusy = false;
    }

    public Task RefreshAsync() =>
        CurrentAccount is { } acc && CurrentFolder.Length > 0
            ? OpenFolderAsync(acc, CurrentFolder, FolderTitle)
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
