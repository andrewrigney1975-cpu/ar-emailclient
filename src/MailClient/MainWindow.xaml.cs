using MailClient.Helpers;
using MailClient.Models;
using MailClient.Services;
using MailClient.ViewModels;
using MailClient.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace MailClient;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        Title = "WinUI3 Mail";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _vm = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _vm;

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 200, max: 460);
        _ = new ColumnSplitterController(ReadingSplitter, ListColumn, invert: false, min: 280, max: 620);

        AccountStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(BuildTree);
        Closed += (_, _) => MailService.DisconnectAll();

        RootGrid.Loaded += (_, _) => BuildTree();
    }

    // ----- rail tree -----

    private void BuildTree()
    {
        MailTree.RootNodes.Clear();
        var accounts = AccountStore.All;

        EmptyRailHint.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MailTree.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var account in accounts)
        {
            MailTree.RootNodes.Add(new TreeViewNode
            {
                Content = new MailNode
                {
                    AccountId = account.Id,
                    IsAccount = true,
                    DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName,
                },
                HasUnrealizedChildren = true,
            });
        }

        if (MailTree.RootNodes.Count > 0)
        {
            MailTree.RootNodes[0].IsExpanded = true;
        }
    }

    private async void MailTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not MailNode { IsAccount: true } node || args.Node.Children.Count > 0)
        {
            return;
        }

        args.Node.HasUnrealizedChildren = false;
        await LoadFoldersAsync(args.Node, node);
    }

    private async Task LoadFoldersAsync(TreeViewNode accountNode, MailNode node)
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
            var folders = await Task.Run(() => MailService.GetFoldersAsync(account, CancellationToken.None));

            accountNode.Children.Clear();
            foreach (var folder in folders)
            {
                var child = new TreeViewNode
                {
                    Content = new MailNode
                    {
                        AccountId = account.Id,
                        IsAccount = false,
                        FolderFullName = folder.FullName,
                        DisplayName = folder.Name,
                        UnreadCount = folder.Unread,
                    },
                };
                accountNode.Children.Add(child);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.LoadFoldersAsync", ex);
            node.Error = ex.Message;
            _ = ShowErrorAsync($"Couldn't connect to {account.Email}", ex.Message);
        }
        finally
        {
            node.IsConnecting = false;
        }
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

    // ----- message list / reading -----

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row)
        {
            await _vm.OpenMessageAsync(row);
            await RenderCurrentMessageAsync();
        }
    }

    private async Task RenderCurrentMessageAsync(bool remoteContent = false)
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
        LoadImagesButton.Visibility = msg.HadRemoteContent && !remoteContent ? Visibility.Visible : Visibility.Collapsed;

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
            await RenderCurrentMessageAsync(remoteContent: true);
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
