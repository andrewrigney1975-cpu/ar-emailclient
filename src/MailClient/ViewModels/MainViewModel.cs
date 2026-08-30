using System.Collections.ObjectModel;
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

    public MainViewModel(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public ObservableCollection<MessageRow> Messages { get; } = new();

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
    public partial MessageRow? SelectedMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial MailMessageContent? CurrentMessage { get; set; }

    public bool HasMessage => CurrentMessage is not null;

    public MailAccount? CurrentAccount { get; private set; }
    public string CurrentFolder { get; private set; } = string.Empty;

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
        SelectedMessage = null;

        Messages.Clear();
        foreach (var row in MessageCache.Load(account.Id, folderFullName))
        {
            Messages.Add(row);
        }

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
                Messages.Clear();
                foreach (var row in live)
                {
                    Messages.Add(row);
                }

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
        StatusText = "Opening...";

        try
        {
            var content = await Task.Run(
                () => MailService.GetMessageAsync(CurrentAccount, row.Folder, row.Uid, allowRemoteContent, ct), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                CurrentMessage = content;
                StatusText = string.Empty;
            });

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

        Messages.Remove(row);
        if (ReferenceEquals(SelectedMessage, row))
        {
            CurrentMessage = null;
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

    public Task RefreshAsync() =>
        CurrentAccount is { } acc && CurrentFolder.Length > 0
            ? OpenFolderAsync(acc, CurrentFolder, FolderTitle)
            : Task.CompletedTask;
}
