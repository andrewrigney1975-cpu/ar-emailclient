using System.Collections.ObjectModel;
using MailClient.Helpers;
using MailClient.Models;
using MailClient.Services;
using MailClient.ViewModels;
using MailClient.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MailClient;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<MailNode> _railNodes = new();

    public MainWindow()
    {
        InitializeComponent();

        MailTree.ItemsSource = _railNodes;

        Title = $"WinUI3 Mail — build {BuildInfo.Number}";
        AppTitleText.Text = $"WinUI3 Mail  ·  build {BuildInfo.Number}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _vm = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _vm;

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 200, max: 460);
        _ = new ColumnSplitterController(ReadingSplitter, ListColumn, invert: false, min: 280, max: 620);

        AccountStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(BuildTree);
        Closed += (_, _) => MailService.DisconnectAll();

        ApplyCalendarVisibility(AppSettings.Current.CalendarVisible);

        RootGrid.Loaded += (_, _) => BuildTree();
    }

    // ----- rail tree -----

    private void BuildTree()
    {
        _railNodes.Clear();
        var accounts = AccountStore.All;

        EmptyRailHint.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MailTree.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var account in accounts)
        {
            var node = new MailNode
            {
                AccountId = account.Id,
                IsAccount = true,
                DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName,
                IsExpanded = true,
            };

            // Show the last-known folder list instantly from the local cache, then refresh live.
            foreach (var cached in MessageCache.LoadFolders(account.Id))
            {
                node.Children.Add(new MailNode
                {
                    AccountId = account.Id,
                    IsAccount = false,
                    FolderFullName = cached.FullName,
                    DisplayName = cached.Name,
                    UnreadCount = cached.Unread,
                });
            }

            _railNodes.Add(node);
            _ = LoadFoldersAsync(node);
        }
    }

    private async Task LoadFoldersAsync(MailNode node)
    {
        var account = AccountStore.Find(node.AccountId);
        if (account is null)
        {
            return;
        }

        node.IsConnecting = true;
        node.Error = null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var folders = await Task.Run(() => MailService.GetFoldersAsync(account, cts.Token), cts.Token)
                .ConfigureAwait(false);

            MessageCache.SaveFolders(account.Id,
                folders.Select(f => new MessageCache.CachedFolder(f.FullName, f.Name, f.Unread)).ToList());

            DispatcherQueue.TryEnqueue(() =>
            {
                node.Children.Clear();
                foreach (var folder in folders)
                {
                    node.Children.Add(new MailNode
                    {
                        AccountId = account.Id,
                        IsAccount = false,
                        FolderFullName = folder.FullName,
                        DisplayName = folder.Name,
                        UnreadCount = folder.Unread,
                    });
                }

                node.IsExpanded = true;
                node.IsConnecting = false;
                LoggingService.Info("MainWindow.LoadFoldersAsync",
                    $"rendered {node.Children.Count} folder node(s) for {account.Email}");
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.LoadFoldersAsync", ex);
            var message = ex is OperationCanceledException
                ? $"Timed out contacting {account.ImapHost}. Check the server address, port and SSL setting."
                : ex.Message;

            DispatcherQueue.TryEnqueue(() =>
            {
                node.Error = message;
                node.IsConnecting = false;
                _ = ShowErrorAsync($"Couldn't connect to {account.Email}", message);
            });
        }
    }

    private void MailTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var node = (e.OriginalSource as FrameworkElement)?.DataContext as MailNode
                   ?? FindNodeInParents(e.OriginalSource as DependencyObject);

        if (node is not { IsAccount: true })
        {
            return;
        }

        var account = AccountStore.Find(node.AccountId);
        if (account is null)
        {
            return;
        }

        var element = e.OriginalSource as FrameworkElement ?? MailTree;
        var flyout = new MenuFlyout();

        var reload = new MenuFlyoutItem { Text = "Reload folders" };
        reload.Click += async (_, _) => await LoadFoldersAsync(node);
        flyout.Items.Add(reload);

        var edit = new MenuFlyoutItem { Text = "Edit account…" };
        edit.Click += async (_, _) => await EditAccountAsync(account);
        flyout.Items.Add(edit);

        var remove = new MenuFlyoutItem { Text = "Remove account…" };
        remove.Click += async (_, _) => await RemoveAccountAsync(account);
        flyout.Items.Add(remove);

        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private static MailNode? FindNodeInParents(DependencyObject? start)
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: MailNode node })
            {
                return node;
            }
        }

        return null;
    }

    private async Task EditAccountAsync(MailAccount account)
    {
        var dialog = new AddAccountDialog(account) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private async Task RemoveAccountAsync(MailAccount account)
    {
        var confirm = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Remove account",
            Content = new TextBlock
            {
                Text = $"Remove {account.Email}? Cached messages for this account will also be cleared.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (confirm != ContentDialogResult.Primary)
        {
            return;
        }

        MailService.Disconnect(account.Id);

        if (_vm.CurrentAccount?.Id == account.Id)
        {
            _vm.ClearView();
        }

        MessageCache.ClearAccount(account.Id);
        AccountStore.Remove(account.Id);
    }

    private async void MailTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not MailNode node)
        {
            return;
        }

        if (node.IsAccount)
        {
            return;
        }

        var account = AccountStore.Find(node.AccountId);
        if (account is not null)
        {
            await _vm.OpenFolderAsync(account, node.FolderFullName, node.DisplayName);
        }
    }

    // ----- search -----

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = (sender.Text ?? string.Empty).Trim();
        var account = _vm.CurrentAccount ?? AccountStore.All.FirstOrDefault();
        if (account is null)
        {
            return;
        }

        if (query.Length == 0)
        {
            await _vm.RefreshAsync();
            return;
        }

        await _vm.SearchAsync(account, query);
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
        {
            await _vm.RefreshAsync();
        }
    }

    // ----- sync / calendar -----

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncRing.IsActive = true;
        SyncRing.Visibility = Visibility.Visible;
        SyncButton.IsEnabled = false;
        try
        {
            foreach (var node in _railNodes.ToList())
            {
                await LoadFoldersAsync(node);
            }

            await _vm.RefreshAsync();
        }
        finally
        {
            SyncRing.IsActive = false;
            SyncRing.Visibility = Visibility.Collapsed;
            SyncButton.IsEnabled = true;
        }
    }

    private void CalendarToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = CalendarPane.Visibility != Visibility.Visible;
        ApplyCalendarVisibility(show);
        AppSettings.Update(s => s.CalendarVisible = show);
    }

    private void ApplyCalendarVisibility(bool visible)
    {
        CalendarPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CalendarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CalendarSplitterColumn.Width = new GridLength(visible ? 6 : 0);
        CalendarColumn.Width = visible ? new GridLength(300) : new GridLength(0);
    }

    // ----- message list / reading -----

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row)
        {
            await _vm.OpenMessageAsync(row);
            await RenderCurrentMessageAsync();
        }
    }

    private void AlwaysLoadImages_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentMessage is { FromAddress.Length: > 0 } msg)
        {
            RemoteContentStore.Allow(msg.FromAddress);
            AlwaysLoadImagesButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RenderCurrentMessageAsync()
    {
        var msg = _vm.CurrentMessage;
        ReadingEmpty.Visibility = msg is null ? Visibility.Visible : Visibility.Collapsed;
        if (msg is null)
        {
            return;
        }

        SubjectText.Text = string.IsNullOrWhiteSpace(msg.Subject) ? "(no subject)" : msg.Subject;
        FromText.Text = "From: " + msg.FromDisplay;
        ToText.Text = string.IsNullOrWhiteSpace(msg.ToDisplay) ? string.Empty : "To: " + msg.ToDisplay;
        DateText.Text = msg.Date.LocalDateTime.ToString("f");

        AttachmentsList.ItemsSource = msg.Attachments;
        AttachmentsList.Visibility = msg.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var domain = RemoteContentStore.DomainOf(msg.FromAddress);
        var domainAlreadyAllowed = RemoteContentStore.IsAllowed(msg.FromAddress);
        LoadImagesButton.Visibility =
            msg.HadRemoteContent && !msg.RemoteContentAllowed ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesButton.Visibility =
            msg.HadRemoteContent && msg.RemoteContentAllowed && !domainAlreadyAllowed && domain.Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesText.Text = $"Always load images from {domain}";

        if (msg.Html is { } html)
        {
            BodyTextScroller.Visibility = Visibility.Collapsed;
            BodyWeb.Visibility = Visibility.Visible;
            try
            {
                await BodyWeb.EnsureCoreWebView2Async();
                BodyWeb.CoreWebView2.Settings.IsScriptEnabled = false;
                BodyWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                BodyWeb.CoreWebView2.Settings.IsStatusBarEnabled = false;
                BodyWeb.CoreWebView2.NavigateToString(WrapHtml(html));
            }
            catch (Exception ex)
            {
                LoggingService.Warn("MainWindow.RenderCurrentMessageAsync (webview)", ex);
                BodyWeb.Visibility = Visibility.Collapsed;
                BodyTextScroller.Visibility = Visibility.Visible;
                BodyText.Text = msg.PlainText ?? "(no readable body)";
            }
        }
        else
        {
            BodyWeb.Visibility = Visibility.Collapsed;
            BodyTextScroller.Visibility = Visibility.Visible;
            BodyText.Text = string.IsNullOrWhiteSpace(msg.PlainText) ? "(no readable body)" : msg.PlainText;
        }
    }

    private static string WrapHtml(string inner) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<style>body{font-family:'Segoe UI',system-ui,sans-serif;margin:16px;overflow-wrap:anywhere;} " +
        "img{max-width:100%;height:auto;} @media (prefers-color-scheme: dark){body{background:#1b1b1b;color:#e6e6e6;}}</style>" +
        "</head><body>" + inner + "</body></html>";

    private async void LoadImages_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row)
        {
            await _vm.OpenMessageAsync(row, allowRemoteContent: true);
            await RenderCurrentMessageAsync();
        }
    }

    // ----- toolbar -----

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => OpenCompose(ComposeMode.New, null);

    private void Reply_Click(object sender, RoutedEventArgs e) => OpenCompose(ComposeMode.Reply, _vm.CurrentMessage);

    private void ReplyAll_Click(object sender, RoutedEventArgs e) => OpenCompose(ComposeMode.ReplyAll, _vm.CurrentMessage);

    private void Forward_Click(object sender, RoutedEventArgs e) => OpenCompose(ComposeMode.Forward, _vm.CurrentMessage);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row)
        {
            await _vm.DeleteAsync(row);
            await RenderCurrentMessageAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private void OpenCompose(ComposeMode mode, MailMessageContent? source)
    {
        var account = _vm.CurrentAccount ?? AccountStore.All.FirstOrDefault();
        if (account is null)
        {
            _ = ShowErrorAsync("No account", "Add an account first.");
            return;
        }

        new ComposeWindow(account, mode, source).Activate();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
        }.ShowAsync();
    }
}
