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
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using MimeKit;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using IoPath = System.IO.Path;
using IoFile = System.IO.File;
using IoDirectory = System.IO.Directory;

namespace MailClient;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<MailNode> _railNodes = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private MailAccount? _composeAccount;
    private MailMessageContent? _composeSource;
    private bool _restoredLastFolder;
    private CalendarSuggestion? _currentSuggestion;
    private DispatcherTimer? _pollTimer;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _vm;

        MailTree.ItemsSource = _railNodes;
        MessageTree.ItemsSource = _vm.ListNodes;

        Title = $"WinUI3 Mail — build {BuildInfo.Number}";
        AppTitleText.Text = $"WinUI3 Mail  ·  build {BuildInfo.Number}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var settings = AppSettings.Current;
        if (settings.RailWidth >= 200)
        {
            RailColumn.Width = new GridLength(settings.RailWidth);
        }

        if (settings.ListWidth >= 280)
        {
            ListColumn.Width = new GridLength(settings.ListWidth);
        }

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 200, max: 460,
            onResized: w => AppSettings.Update(s => s.RailWidth = w));
        _ = new ColumnSplitterController(ReadingSplitter, ListColumn, invert: false, min: 280, max: 620,
            onResized: w => AppSettings.Update(s => s.ListWidth = w));

        AccountStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(BuildTree);
        CalendarStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshCalendarDay);
        Closed += (_, _) =>
        {
            _pollTimer?.Stop();
            MailService.DisconnectAll();
        };

        ApplyCalendarVisibility(settings.CalendarVisible);

        RootGrid.Loaded += (_, _) =>
        {
            BuildTree();
            RailCalendar.SetDisplayDate(DateTimeOffset.Now);
            RefreshCalendarDay();
            StartPolling();
        };
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) => await _vm.RefreshAsync(quiet: true);
        _pollTimer.Start();
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

            // Open the last-used folder straight from the local cache before any sync happens.
            TryRestoreLastFolder(account, node);

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

                TryRestoreLastFolder(account, node);
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

    private void TryRestoreLastFolder(MailAccount account, MailNode accountNode)
    {
        if (_restoredLastFolder || _vm.CurrentFolder.Length > 0)
        {
            return;
        }

        var settings = AppSettings.Current;
        if (settings.LastAccountId != account.Id || settings.LastFolder.Length == 0)
        {
            return;
        }

        var folder = accountNode.Children.FirstOrDefault(c => c.FolderFullName == settings.LastFolder);
        if (folder is null)
        {
            return;
        }

        _restoredLastFolder = true;
        _ = _vm.OpenFolderAsync(account, folder.FolderFullName, folder.DisplayName);
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
        CalendarColumn.Width = visible ? new GridLength(336) : new GridLength(0);
    }

    private void RailCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args) =>
        RefreshCalendarDay();

    private void RailCalendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Phase == 0)
        {
            args.Item.SetDensityColors(CalendarStore.AnyOn(args.Item.Date)
                ? new[] { (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"] }
                : null);
        }
    }

    private void CalendarEventDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            CalendarStore.Remove(id);
        }
    }

    private void RefreshCalendarDay()
    {
        var day = RailCalendar.SelectedDates.Count > 0 ? RailCalendar.SelectedDates[0] : DateTimeOffset.Now;
        var events = CalendarStore.ForDay(day);

        CalendarDayHeader.Text = day.LocalDateTime.ToString("dddd, d MMM yyyy");
        CalendarDayEvents.ItemsSource = events;
        CalendarDayEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Nudge the day items so their event-density dots repaint.
        RailCalendar.SetDisplayDate(day);
    }

    private async void AddToCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSuggestion is not { } suggestion)
        {
            return;
        }

        var titleBox = new TextBox { Header = "Title", Text = suggestion.Title };
        var datePicker = new CalendarDatePicker { Header = "Date", Date = suggestion.Date };
        var notesBox = new TextBox
        {
            Header = "Notes",
            Text = _vm.CurrentMessage?.Subject ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84,
        };

        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(titleBox);
        panel.Children.Add(datePicker);
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add calendar event",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var date = datePicker.Date ?? suggestion.Date;
        CalendarStore.Add(new CalendarEvent
        {
            Date = date,
            Title = string.IsNullOrWhiteSpace(titleBox.Text) ? suggestion.Title : titleBox.Text.Trim(),
            Notes = notesBox.Text.Trim(),
        });

        if (CalendarPane.Visibility != Visibility.Visible)
        {
            ApplyCalendarVisibility(true);
            AppSettings.Update(s => s.CalendarVisible = true);
        }

        RailCalendar.SelectedDates.Clear();
        RailCalendar.SelectedDates.Add(date);
        RefreshCalendarDay();
    }

    // ----- message list / reading -----

    private enum ReadingMode { Empty, Message, Compose, Preview }

    private void ShowReading(ReadingMode mode)
    {
        ReadingEmpty.Visibility = mode == ReadingMode.Empty ? Visibility.Visible : Visibility.Collapsed;
        ReadingContent.Visibility = mode == ReadingMode.Message ? Visibility.Visible : Visibility.Collapsed;
        ReadingCompose.Visibility = mode == ReadingMode.Compose ? Visibility.Visible : Visibility.Collapsed;
        ReadingPreview.Visibility = mode == ReadingMode.Preview ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MessageTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not MailListNode node)
        {
            return;
        }

        if (node.Kind == MailListKind.Message && node.Row is { } row)
        {
            await _vm.OpenMessageAsync(row);
            await RenderCurrentMessageAsync();
            return;
        }

        // Date group / thread header: toggle its section.
        node.IsExpanded = !node.IsExpanded;
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
        if (msg is null)
        {
            if (ReadingCompose.Visibility != Visibility.Visible &&
                ReadingPreview.Visibility != Visibility.Visible)
            {
                ShowReading(ReadingMode.Empty);
            }

            return;
        }

        ShowReading(ReadingMode.Message);

        SubjectText.Text = string.IsNullOrWhiteSpace(msg.Subject) ? "(no subject)" : msg.Subject;
        FromText.Text = "From: " + msg.FromDisplay;
        ToText.Text = string.IsNullOrWhiteSpace(msg.ToDisplay) ? string.Empty : "To: " + msg.ToDisplay;
        DateText.Text = msg.Date.LocalDateTime.ToString("f");

        AttachmentsList.ItemsSource = msg.Attachments;
        AttachmentsBar.Visibility = msg.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var domain = RemoteContentStore.DomainOf(msg.FromAddress);
        var domainAlreadyAllowed = RemoteContentStore.IsAllowed(msg.FromAddress);
        LoadImagesButton.Visibility =
            msg.HadRemoteContent && !msg.RemoteContentAllowed ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesButton.Visibility =
            msg.HadRemoteContent && msg.RemoteContentAllowed && !domainAlreadyAllowed && domain.Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesText.Text = $"Always load images from {domain}";

        _currentSuggestion = DateActionScanner.Scan(msg.Subject, msg.Html ?? msg.PlainText, msg.FromAddress);
        AddToCalendarButton.Visibility = _currentSuggestion is null ? Visibility.Collapsed : Visibility.Visible;
        if (_currentSuggestion is { } sg)
        {
            AddToCalendarText.Text = $"Add to calendar: {sg.Title} ({sg.Date.LocalDateTime:d MMM})";
        }

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
        if (_vm.CurrentOpenRow is { } row)
        {
            await _vm.OpenMessageAsync(row, allowRemoteContent: true);
            await RenderCurrentMessageAsync();
        }
    }

    // ----- attachments -----

    private string? _previewName;
    private byte[]? _previewData;
    private string? _previewTempPath;

    private async void AttachmentPreview_Click(object sender, RoutedEventArgs e)
    {
        var (name, data) = await FetchAttachmentAsync(sender);
        if (data is null || name is null)
        {
            return;
        }

        _previewName = name;
        _previewData = data;
        _previewTempPath = null;
        PreviewTitle.Text = name;

        PreviewImageScroller.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewWeb.Visibility = Visibility.Collapsed;
        PreviewTextScroller.Visibility = Visibility.Collapsed;
        PreviewFallback.Visibility = Visibility.Collapsed;

        var ext = IoPath.GetExtension(name).ToLowerInvariant();
        try
        {
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico")
            {
                PreviewImage.Source = new BitmapImage(new Uri(await PreviewTempFileAsync()));
                PreviewImageScroller.Visibility = Visibility.Visible;
            }
            else if (ext is ".pdf" or ".html" or ".htm" or ".svg")
            {
                await PreviewWeb.EnsureCoreWebView2Async();
                PreviewWeb.Source = new Uri(await PreviewTempFileAsync());
                PreviewWeb.Visibility = Visibility.Visible;
            }
            else if (ext is ".txt" or ".csv" or ".log" or ".md" or ".json" or ".xml" or ".ini"
                     or ".yml" or ".yaml" or ".cs" or ".js" or ".ts" or ".py" or ".c" or ".h" or ".cpp")
            {
                var text = System.Text.Encoding.UTF8.GetString(data);
                PreviewText.Text = text.Length > 200_000 ? text[..200_000] + "\n…" : text;
                PreviewTextScroller.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewFallbackText.Text = $"No in-app preview for {(ext.Length > 1 ? ext : "this")} files.";
                PreviewFallback.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.AttachmentPreview_Click", ex);
            PreviewFallbackText.Text = "Couldn't render a preview: " + ex.Message;
            PreviewFallback.Visibility = Visibility.Visible;
        }

        ShowReading(ReadingMode.Preview);
    }

    /// Writes the current preview payload to a temp file once, returning its path.
    private async Task<string> PreviewTempFileAsync()
    {
        if (_previewTempPath is not null)
        {
            return _previewTempPath;
        }

        var path = IoPath.Combine(IoPath.GetTempPath(), "WinUI3Mail", _previewName!);
        IoDirectory.CreateDirectory(IoPath.GetDirectoryName(path)!);
        await IoFile.WriteAllBytesAsync(path, _previewData!);
        return _previewTempPath = path;
    }

    private void PreviewBack_Click(object sender, RoutedEventArgs e)
    {
        PreviewWeb.Source = new Uri("about:blank");
        PreviewImage.Source = null;
        ShowReading(_vm.HasMessage ? ReadingMode.Message : ReadingMode.Empty);
    }

    private async void PreviewOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_previewData is null)
        {
            return;
        }

        try
        {
            await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(await PreviewTempFileAsync()));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.PreviewOpenExternal_Click", ex);
            await ShowErrorAsync("Couldn't open attachment", ex.Message);
        }
    }

    private async void PreviewSave_Click(object sender, RoutedEventArgs e)
    {
        if (_previewName is not null && _previewData is not null)
        {
            await SaveBytesAsync(_previewName, _previewData);
        }
    }

    private async void AttachmentDownload_Click(object sender, RoutedEventArgs e)
    {
        var (name, data) = await FetchAttachmentAsync(sender);
        if (data is not null && name is not null)
        {
            await SaveBytesAsync(name, data);
        }
    }

    private async Task SaveBytesAsync(string name, byte[] data)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads };
            var ext = IoPath.GetExtension(name) is { Length: > 1 } x ? x : ".dat";
            picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant() + " file", new List<string> { ext });
            picker.SuggestedFileName = IoPath.GetFileNameWithoutExtension(name);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await FileIO.WriteBytesAsync(file, data);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.AttachmentDownload_Click", ex);
            await ShowErrorAsync("Couldn't save attachment", ex.Message);
        }
    }

    private async Task<(string? Name, byte[]? Data)> FetchAttachmentAsync(object sender)
    {
        if (sender is not FrameworkElement { Tag: int index } ||
            _vm.CurrentAccount is not { } account ||
            _vm.CurrentOpenRow is not { } row)
        {
            return (null, null);
        }

        try
        {
            return await Task.Run(() =>
                MailService.GetAttachmentAsync(account, row.Folder, row.Uid, index, CancellationToken.None));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.FetchAttachmentAsync", ex);
            await ShowErrorAsync("Couldn't download attachment", ex.Message);
            return (null, null);
        }
    }

    // ----- toolbar / compose -----

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => StartCompose(ComposeMode.New, null);

    private void Reply_Click(object sender, RoutedEventArgs e) => StartCompose(ComposeMode.Reply, _vm.CurrentMessage);

    private void ReplyAll_Click(object sender, RoutedEventArgs e) => StartCompose(ComposeMode.ReplyAll, _vm.CurrentMessage);

    private void Forward_Click(object sender, RoutedEventArgs e) => StartCompose(ComposeMode.Forward, _vm.CurrentMessage);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentOpenRow is { } row)
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

    private void StartCompose(ComposeMode mode, MailMessageContent? source)
    {
        var account = _vm.CurrentAccount ?? AccountStore.All.FirstOrDefault();
        if (account is null)
        {
            _ = ShowErrorAsync("No account", "Add an account first.");
            return;
        }

        _composeAccount = account;
        _composeSource = mode == ComposeMode.New ? null : source;

        ComposeStatus.IsOpen = false;
        ComposeSendButton.IsEnabled = true;
        ComposeTo.Text = ComposeCc.Text = ComposeSubject.Text = ComposeBody.Text = string.Empty;

        if (_composeSource is { } src)
        {
            var quoted = string.Join("\n", (src.PlainText ?? StripHtml(src.Html) ?? string.Empty)
                .Split('\n').Select(l => "> " + l));
            var replyBody = $"\n\nOn {src.Date.LocalDateTime:f}, {src.FromDisplay} wrote:\n{quoted}\n";

            switch (mode)
            {
                case ComposeMode.Reply:
                    ComposeTo.Text = src.ReplyToAddress;
                    ComposeSubject.Text = Prefixed("Re:", src.Subject);
                    ComposeBody.Text = replyBody;
                    break;
                case ComposeMode.ReplyAll:
                    ComposeTo.Text = src.ReplyToAddress;
                    ComposeCc.Text = src.CcDisplay;
                    ComposeSubject.Text = Prefixed("Re:", src.Subject);
                    ComposeBody.Text = replyBody;
                    break;
                case ComposeMode.Forward:
                    ComposeSubject.Text = Prefixed("Fwd:", src.Subject);
                    ComposeBody.Text =
                        $"\n\n---------- Forwarded message ----------\nFrom: {src.FromDisplay}\n" +
                        $"Date: {src.Date.LocalDateTime:f}\nSubject: {src.Subject}\nTo: {src.ToDisplay}\n\n" +
                        (src.PlainText ?? StripHtml(src.Html) ?? string.Empty);
                    break;
            }
        }

        ComposeHeading.Text = mode switch
        {
            ComposeMode.Reply or ComposeMode.ReplyAll => "Reply",
            ComposeMode.Forward => "Forward",
            _ => "New message",
        };

        ShowReading(ReadingMode.Compose);
        ComposeTo.Focus(FocusState.Programmatic);
    }

    private void ComposeDiscard_Click(object sender, RoutedEventArgs e) =>
        ShowReading(_vm.HasMessage ? ReadingMode.Message : ReadingMode.Empty);

    private async void ComposeSend_Click(object sender, RoutedEventArgs e)
    {
        if (_composeAccount is not { } account)
        {
            return;
        }

        var recipients = ParseAddresses(ComposeTo.Text).ToList();
        if (recipients.Count == 0)
        {
            ComposeStatus.Severity = InfoBarSeverity.Warning;
            ComposeStatus.Message = "Add at least one recipient.";
            ComposeStatus.IsOpen = true;
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName, account.Email));
        message.To.AddRange(recipients);
        message.Cc.AddRange(ParseAddresses(ComposeCc.Text));
        message.Subject = ComposeSubject.Text;
        message.Body = new TextPart("plain") { Text = ComposeBody.Text };

        if (_composeSource is { MessageId.Length: > 0 } src)
        {
            message.InReplyTo = src.MessageId;
            foreach (var reference in src.References.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                message.References.Add(reference);
            }

            message.References.Add(src.MessageId);
        }

        ComposeSendButton.IsEnabled = false;
        ComposeStatus.Severity = InfoBarSeverity.Informational;
        ComposeStatus.Message = "Sending...";
        ComposeStatus.IsOpen = true;

        try
        {
            await Task.Run(() => MailService.SendAsync(account, message, CancellationToken.None));
            ShowReading(_vm.HasMessage ? ReadingMode.Message : ReadingMode.Empty);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.ComposeSend_Click", ex);
            ComposeStatus.Severity = InfoBarSeverity.Error;
            ComposeStatus.Message = "Send failed: " + ex.Message;
            ComposeStatus.IsOpen = true;
            ComposeSendButton.IsEnabled = true;
        }
    }

    private static IEnumerable<MailboxAddress> ParseAddresses(string raw)
    {
        foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MailboxAddress.TryParse(part.Trim(), out var address))
            {
                yield return address;
            }
        }
    }

    private static string Prefixed(string prefix, string subject) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? subject : $"{prefix} {subject}";

    private static string? StripHtml(string? html) =>
        html is null ? null : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty);

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
