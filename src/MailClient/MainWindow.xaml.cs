using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
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
    private readonly ObservableCollection<OutgoingAttachment> _composeAttachments = new();
    private bool _composeEditorReady;
    private string _pendingEditorHtml = string.Empty;
    private bool _restoredLastFolder;
    private CalendarSuggestion? _currentSuggestion;
    private DispatcherTimer? _pollTimer;
    private MailRef? _pendingNotificationMail;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _vm;

        MailTree.ItemsSource = _railNodes;
        MessageTree.ItemsSource = _vm.ListNodes;
        ComposeAttachmentsList.ItemsSource = _composeAttachments;
        ComposeEditor.NavigationCompleted += (_, _) =>
        {
            _composeEditorReady = true;
            _ = SetComposeHtmlAsync(_pendingEditorHtml);
        };

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

        NotificationService.MailActivated += OnMailActivated;
        AccountStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(BuildTree);
        MessageCache.TagsChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshTagNodes();
            RefreshMessageTags();
        });
        MessageCache.FavouritesChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshFavouriteButton);
        Services.Ai.Ai.ReadyChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshSummariseButton);
        MessageCache.FollowsChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshFlagButton();
            if (_vm.SmartView == "followups")
            {
                _ = _vm.RefreshAsync();
            }
        });
        CalendarStore.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshCalendarDay();
            CheckEventReminders();
        });
        Closed += (_, _) =>
        {
            _pollTimer?.Stop();
            NotificationService.MailActivated -= OnMailActivated;
            MailService.DisconnectAll();
            NotificationService.Unregister();
        };

        ApplyCalendarVisibility(settings.CalendarVisible);

        RootGrid.Loaded += async (_, _) =>
        {
            BuildTree();
            RailCalendar.SetDisplayDate(DateTimeOffset.Now);
            RefreshCalendarDay();
            CheckEventReminders();
            StartPolling();

            ContactStore.RefreshFromCache();
            _ = Services.Ai.AiBootstrapper.RefreshAsync(DispatcherQueue);

            _loaded = true;
            if (_pendingNotificationMail is { } pending)
            {
                _pendingNotificationMail = null;
                await OpenMailFromNotificationAsync(pending);
            }
        };
    }

    /// Entry point for a "new mail" toast click (App routes cold-launch activations here).
    public void OpenFromNotification(MailRef reference)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_loaded)
            {
                _pendingNotificationMail = reference;
                return;
            }

            await OpenMailFromNotificationAsync(reference);
        });
    }

    private void OnMailActivated(MailRef reference) => OpenFromNotification(reference);

    private async Task OpenMailFromNotificationAsync(MailRef reference)
    {
        BringToForeground();

        var account = AccountStore.Find(reference.AccountId);
        if (account is null)
        {
            return;
        }

        var folderNode = _railNodes
            .SelectMany(n => n.Children)
            .FirstOrDefault(c => c.AccountId == reference.AccountId && c.FolderFullName == reference.Folder);

        await _vm.OpenFolderAsync(account, reference.Folder, folderNode?.DisplayName ?? reference.Folder);

        var row = _vm.FindRow(reference.Folder, reference.Uid)
                  ?? new MessageRow { AccountId = reference.AccountId, Folder = reference.Folder, Uid = reference.Uid };

        await _vm.OpenMessageAsync(row);
        await RenderCurrentMessageAsync();
    }

    internal void BringToForeground()
    {
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            AppWindow?.Show();
            Activate();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.BringToForeground", ex);
        }
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) =>
        {
            await _vm.RefreshAsync(quiet: true);
            CheckEventReminders();
            ContactStore.RefreshFromCache();
        };
        _pollTimer.Start();
    }

    /// Toast for any calendar event that is exactly one day away, once per event.
    private void CheckEventReminders()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var alreadyNotified = AppSettings.Current.NotifiedReminderIds;

        var due = CalendarStore.All
            .Where(ev => ev.Date.LocalDateTime.Date == tomorrow && !alreadyNotified.Contains(ev.Id))
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        foreach (var ev in due)
        {
            NotificationService.Show("Reminder — tomorrow", $"{ev.Title} ({ev.Date.LocalDateTime:ddd d MMM})");
        }

        AppSettings.Update(s =>
        {
            foreach (var ev in due)
            {
                if (!s.NotifiedReminderIds.Contains(ev.Id))
                {
                    s.NotifiedReminderIds.Add(ev.Id);
                }
            }
        });
    }

    private const string TagGlyph = "";

    /// Rebuilds the per-tag children under Smart Folders > Tags (no folder reload).
    private void RefreshTagNodes()
    {
        var tagsNode = _railNodes
            .FirstOrDefault(n => n.AccountId == "__smart__")?.Children
            .FirstOrDefault(c => c.FolderFullName == "__tags__");
        if (tagsNode is null)
        {
            return;
        }

        var tags = MessageCache.AllTags();
        tagsNode.DisplayName = tags.Count > 0 ? $"Tags ({tags.Count})" : "Tags";
        tagsNode.Children.Clear();
        foreach (var tag in tags)
        {
            tagsNode.Children.Add(new MailNode
            {
                AccountId = "__smart__",
                IsAccount = false,
                IsSmart = true,
                FolderFullName = "__tag__:" + tag,
                DisplayName = "#" + tag,
                GlyphOverride = TagGlyph,
            });
        }
    }

    private void RefreshMessageTags()
    {
        if (_vm.CurrentOpenRow is { } row &&
            (_vm.CurrentAccount ?? AccountStore.Find(row.AccountId)) is { } account)
        {
            MessageTagsList.ItemsSource = MessageCache.TagsFor(account.Id, row.Folder, row.Uid);
        }
        else
        {
            MessageTagsList.ItemsSource = null;
        }
    }

    private async Task AddTagToRowAsync(MessageRow row)
    {
        var box = new AutoSuggestBox
        {
            Header = "Tag",
            PlaceholderText = "e.g. rego",
            Width = 280,
            ItemsSource = MessageCache.AllTags(),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add tag",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            MessageCache.AddTag(row.AccountId, row.Folder, row.Uid, box.Text);
        }
    }

    private async void MessageTagAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentOpenRow is { } row)
        {
            await AddTagToRowAsync(row);
        }
    }

    private void MessageTagRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _vm.CurrentOpenRow is { } row)
        {
            MessageCache.RemoveTag(row.AccountId, row.Folder, row.Uid, tag);
        }
    }

    private void Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentOpenRow is { } row &&
            (_vm.CurrentAccount ?? AccountStore.Find(row.AccountId)) is { } account)
        {
            _vm.SetFavourite(row, !MessageCache.IsFavourite(account.Id, row.Folder, row.Uid));
            RefreshFavouriteButton();
        }
    }

    private void RefreshFavouriteButton()
    {
        var fav = _vm.CurrentOpenRow is { } row &&
                  (_vm.CurrentAccount ?? AccountStore.Find(row.AccountId)) is { } account &&
                  MessageCache.IsFavourite(account.Id, row.Folder, row.Uid);
        FavouriteGlyph.Glyph = fav ? "" : "";
    }


    private void RefreshFlagButton()
    {
        var open = _vm.CurrentOpenRow is { } row
            ? MessageCache.FollowFor(row.AccountId, row.Folder, row.Uid)
            : null;
        FlagButton.Opacity = open is { Done: false } ? 1.0 : 0.65;
        FlagGlyph.Foreground = open is { Done: false }
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void RefreshPriorityBadge()
    {
        var p = _vm.CurrentMessage?.Priority ?? 1;
        if (p >= 2)
        {
            PriorityBadge.Text = "High priority";
            PriorityBadge.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 0xD1, 0x34, 0x38));
            PriorityBadge.Visibility = Visibility.Visible;
        }
        else if (p <= 0)
        {
            PriorityBadge.Text = "Low priority";
            PriorityBadge.Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            PriorityBadge.Visibility = Visibility.Visible;
        }
        else
        {
            PriorityBadge.Visibility = Visibility.Collapsed;
        }
    }

    private async void Flag_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentOpenRow is not { } row ||
            (_vm.CurrentAccount ?? AccountStore.Find(row.AccountId)) is not { } account)
        {
            return;
        }

        var existing = MessageCache.FollowFor(row.AccountId, row.Folder, row.Uid);
        if (existing is { Done: false })
        {
            var flyout = new MenuFlyout();
            var complete = new MenuFlyoutItem { Text = "Mark complete" };
            complete.Click += (_, _) => CompleteFollowUp(account, row, existing);
            var remove = new MenuFlyoutItem { Text = "Remove flag" };
            remove.Click += (_, _) => RemoveFollowUp(account, row, existing);
            flyout.Items.Add(complete);
            flyout.Items.Add(remove);
            flyout.ShowAt(FlagButton);
            return;
        }

        var picker = new CalendarDatePicker { Header = "Due date", Date = DateTimeOffset.Now.AddDays(1) };
        var noteBox = new TextBox
        {
            Header = "Note (optional)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
        };
        var panel = new StackPanel { Spacing = 10, Width = 300 };
        panel.Children.Add(picker);
        panel.Children.Add(noteBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Flag for follow-up",
            Content = panel,
            PrimaryButtonText = "Flag",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var due = picker.Date ?? DateTimeOffset.Now.AddDays(1);
        var subject = _vm.CurrentMessage?.Subject ?? row.Subject;

        var calEvent = new CalendarEvent
        {
            Date = due,
            Title = string.IsNullOrWhiteSpace(subject) ? "Follow up" : $"Follow up: {subject}",
            Notes = string.IsNullOrWhiteSpace(noteBox.Text) ? $"From {row.From}" : noteBox.Text.Trim(),
            SourceAccountId = row.AccountId,
            SourceFolder = row.Folder,
            SourceUid = row.Uid,
        };
        CalendarStore.Add(calEvent);
        MessageCache.SetFollow(row.AccountId, row.Folder, row.Uid, due.UtcTicks, calEvent.Id);
        _ = Task.Run(() => MailService.SetFlaggedAsync(account, row.Folder, row.Uid, true, CancellationToken.None));

        RefreshFlagButton();
    }

    private void CompleteFollowUp(MailAccount account, MessageRow row, MessageCache.FollowInfo follow)
    {
        MessageCache.CompleteFollow(row.AccountId, row.Folder, row.Uid);
        CalendarStore.SetDone(follow.EventId, true);
        _ = Task.Run(() => MailService.SetFlaggedAsync(account, row.Folder, row.Uid, false, CancellationToken.None));
        RefreshFlagButton();
    }

    private void RemoveFollowUp(MailAccount account, MessageRow row, MessageCache.FollowInfo follow)
    {
        MessageCache.RemoveFollow(row.AccountId, row.Folder, row.Uid);
        CalendarStore.Remove(follow.EventId);
        _ = Task.Run(() => MailService.SetFlaggedAsync(account, row.Folder, row.Uid, false, CancellationToken.None));
        RefreshFlagButton();
    }

    private void CalendarEventDone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } box)
        {
            return;
        }

        var done = box.IsChecked == true;
        CalendarStore.SetDone(id, done);

        if (CalendarStore.Find(id) is { SourceAccountId.Length: > 0 } ev)
        {
            MessageCache.CompleteFollow(ev.SourceAccountId, ev.SourceFolder, ev.SourceUid, done);
        }
    }


    // ----- rail tree -----

    private void BuildTree()
    {
        _railNodes.Clear();
        var accounts = AccountStore.All;

        EmptyRailHint.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MailTree.Visibility = Visibility.Visible;

        var smart = new MailNode
        {
            AccountId = "__smart__",
            IsAccount = true,
            IsSmart = true,
            DisplayName = "Smart Folders",
            GlyphOverride = "",
            IsExpanded = true,
        };

        static MailNode SmartChild(string key, string name, string glyph, bool expanded = true) => new()
        {
            AccountId = "__smart__",
            IsAccount = false,
            IsSmart = true,
            FolderFullName = key,
            DisplayName = name,
            GlyphOverride = glyph,
            IsExpanded = expanded,
        };

        smart.Children.Add(SmartChild("__inbox__", "Inbox", ""));
        smart.Children.Add(SmartChild("__unread__", "Unread Mail", ""));
        smart.Children.Add(SmartChild("__sent__", "Sent Items", ""));
        smart.Children.Add(SmartChild("__favourites__", "Favourites", ""));
        smart.Children.Add(SmartChild("__followups__", "Follow Up", ""));
        smart.Children.Add(SmartChild("__tags__", "Tags", "", expanded: false));
        _railNodes.Add(smart);
        RefreshTagNodes();

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
                folders.Select(f => new MessageCache.CachedFolder(f.FullName, f.Name, f.Unread, f.Role)).ToList());

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

        if (node is not { IsAccount: true } || node.IsSmart)
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

        if (node.IsSmart)
        {
            switch (node.FolderFullName)
            {
                case "__unread__": await _vm.ShowUnreadAsync(); break;
                case "__inbox__": await _vm.ShowRoleAsync("inbox", "Inbox"); break;
                case "__sent__": await _vm.ShowRoleAsync("sent", "Sent Items"); break;
                case "__favourites__": await _vm.ShowFavouritesAsync(); break;
                case "__followups__": await _vm.ShowFollowUpsAsync(); break;
                case { } f when f.StartsWith("__tag__:", StringComparison.Ordinal):
                    await _vm.ShowTagAsync(f["__tag__:".Length..]); break;
                default: return;
            }

            await RenderCurrentMessageAsync();
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

        var today = DateTime.Today;
        var groups = CalendarStore.All
            .Where(ev => !ev.Done && ev.Date.LocalDateTime.Date >= today)
            .OrderBy(ev => ev.Date)
            .GroupBy(ev => new DateTime(ev.Date.Year, ev.Date.Month, 1))
            .Select(g => new CalendarMonthGroup
            {
                Header = g.Key.ToString("MMMM yyyy"),
                Events = g.ToList(),
            })
            .ToList();

        UpcomingEvents.ItemsSource = groups;
        UpcomingEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Nudge the day items so their event-density dots repaint.
        RailCalendar.SetDisplayDate(day);
    }

    private void SetCalendarSuggestion(CalendarSuggestion? suggestion)
    {
        _currentSuggestion = suggestion;
        AddToCalendarButton.Visibility = suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        if (suggestion is { } sg)
        {
            AddToCalendarText.Text = $"Add to calendar: {sg.Title} ({sg.Date.LocalDateTime:d MMM})";
        }
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

    private void MessageTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var node = FindListNode(e.OriginalSource as DependencyObject);
        if (node is null)
        {
            return;
        }

        var flyout = new MenuFlyout();

        if (node is { Kind: MailListKind.Message, Row: { } row })
        {
            var read = new MenuFlyoutItem { Text = "Mark as read", IsEnabled = !row.IsRead };
            read.Click += (_, _) => _vm.SetRead(row, true);
            var unread = new MenuFlyoutItem { Text = "Mark as unread", IsEnabled = row.IsRead };
            unread.Click += (_, _) => _vm.SetRead(row, false);
            flyout.Items.Add(read);
            flyout.Items.Add(unread);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var fav = MessageCache.IsFavourite(row.AccountId, row.Folder, row.Uid);
            var favItem = new MenuFlyoutItem { Text = fav ? "Remove from favourites" : "Add to favourites" };
            favItem.Click += (_, _) => _vm.SetFavourite(row, !fav);
            flyout.Items.Add(favItem);

            var follow = MessageCache.FollowFor(row.AccountId, row.Folder, row.Uid);
            if (follow is { Done: false } && AccountStore.Find(row.AccountId) is { } followAcc)
            {
                var done = new MenuFlyoutItem { Text = "Mark follow-up complete" };
                done.Click += (_, _) => CompleteFollowUp(followAcc, row, follow);
                flyout.Items.Add(done);
            }
            else
            {
                var flagItem = new MenuFlyoutItem { Text = "Flag for follow-up…" };
                flagItem.Click += async (_, _) =>
                {
                    await _vm.OpenMessageAsync(row);
                    await RenderCurrentMessageAsync();
                    Flag_Click(FlagButton, new RoutedEventArgs());
                };
                flyout.Items.Add(flagItem);
            }

            var addTag = new MenuFlyoutItem { Text = "Add tag…" };
            addTag.Click += async (_, _) => await AddTagToRowAsync(row);
            flyout.Items.Add(addTag);

            var existing = MessageCache.TagsFor(row.AccountId, row.Folder, row.Uid);
            if (existing.Count > 0)
            {
                var removeSub = new MenuFlyoutSubItem { Text = "Remove tag" };
                foreach (var tag in existing)
                {
                    var item = new MenuFlyoutItem { Text = "#" + tag };
                    var captured = tag;
                    item.Click += (_, _) => MessageCache.RemoveTag(row.AccountId, row.Folder, row.Uid, captured);
                    removeSub.Items.Add(item);
                }

                flyout.Items.Add(removeSub);
            }
        }
        else
        {
            var markGroup = new MenuFlyoutItem
            {
                Text = node.Kind == MailListKind.Thread ? "Mark conversation as read" : "Mark these as read",
            };
            markGroup.Click += (_, _) => MarkNodeRead(node);
            flyout.Items.Add(markGroup);
        }

        var element = e.OriginalSource as FrameworkElement ?? MessageTree;
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private static MailListNode? FindListNode(DependencyObject? start)
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: MailListNode node })
            {
                return node;
            }
        }

        return null;
    }

    private void MarkNodeRead(MailListNode node)
    {
        if (node.Kind == MailListKind.Message && node.Row is { } row)
        {
            _vm.SetRead(row, true);
            return;
        }

        foreach (var child in node.Children.ToList())
        {
            MarkNodeRead(child);
        }
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e) => await _vm.MarkAllReadAsync();

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

        ContactStore.Record(msg.FromAddress, msg.FromDisplay.Split('<')[0].Trim());
        foreach (var address in $"{msg.ToDisplay},{msg.CcDisplay}".Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            ContactStore.Record(address.Trim(), null);
        }

        RefreshMessageTags();
        RefreshFavouriteButton();
        RefreshFlagButton();
        RefreshPriorityBadge();
        RefreshSummariseButton();

        var cachedSummary = _vm.CurrentOpenRow is { } sRow ? MessageCache.AiSummaryFor(sRow.AccountId, sRow.Folder, sRow.Uid) : null;
        if (!string.IsNullOrWhiteSpace(cachedSummary))
        {
            SummaryText.Text = cachedSummary;
            SummaryPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SummaryPanel.Visibility = Visibility.Collapsed;
        }

        var cachedReplies = _vm.CurrentOpenRow is { } rRow
            ? MessageCache.AiRepliesFor(rRow.AccountId, rRow.Folder, rRow.Uid)
            : new List<string>();
        RepliesList.ItemsSource = cachedReplies;
        RepliesPanel.Visibility = cachedReplies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var domain = RemoteContentStore.DomainOf(msg.FromAddress);
        var domainAlreadyAllowed = RemoteContentStore.IsAllowed(msg.FromAddress);
        LoadImagesButton.Visibility =
            msg.HadRemoteContent && !msg.RemoteContentAllowed ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesButton.Visibility =
            msg.HadRemoteContent && msg.RemoteContentAllowed && !domainAlreadyAllowed && domain.Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        AlwaysLoadImagesText.Text = $"Always load images from {domain}";

        var regexSuggestion = DateActionScanner.Scan(msg.Subject, msg.Html ?? msg.PlainText, msg.FromAddress);
        CalendarSuggestion? aiSuggestion = null;
        if (_vm.CurrentOpenRow is { } sgRow)
        {
            var (ticks, title) = MessageCache.AiEventFor(sgRow.AccountId, sgRow.Folder, sgRow.Uid);
            if (ticks > 0)
            {
                aiSuggestion = new CalendarSuggestion(new DateTimeOffset(ticks, TimeSpan.Zero), title);
            }
        }

        SetCalendarSuggestion(aiSuggestion ?? regexSuggestion);

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

    private async void AiButton_Click(object sender, RoutedEventArgs e)
    {
        await new AiSettingsDialog { XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    private void RefreshSummariseButton()
    {
        var show = Services.Ai.Ai.Service.IsReady && _vm.HasMessage ? Visibility.Visible : Visibility.Collapsed;
        SummariseButton.Visibility = show;
        SuggestRepliesButton.Visibility = show;
    }

    private static string PlainBody(MailMessageContent msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.PlainText))
        {
            return msg.PlainText!.Trim();
        }

        var html = msg.Html ?? string.Empty;
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(style|script|head)[^>]*>.*?</\1>",
            " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return System.Text.RegularExpressions.Regex.Replace(html, @"[ \t ]+", " ")
            .Replace("\r", string.Empty)
            .Trim();
    }

    private async void Summarise_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentMessage is not { } msg || _vm.CurrentOpenRow is not { } row ||
            !Services.Ai.Ai.Service.IsReady)
        {
            return;
        }

        SummariseButton.IsEnabled = false;
        SummariseButtonText.Text = "Summarising…";
        SummaryPanel.Visibility = Visibility.Visible;
        SummaryText.Text = string.Empty;

        var body = PlainBody(msg);
        LoggingService.Info("MainWindow.Summarise", $"body {body.Length} chars, model {Services.Ai.Ai.Service.ModelName}");
        var prompt = Services.Ai.AiPrompts.Summarise(msg.Subject, msg.FromDisplay, body);
        var builder = new System.Text.StringBuilder();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await foreach (var piece in Services.Ai.Ai.Service.StreamAsync(prompt, cts.Token))
            {
                builder.Append(piece);
                SummaryText.Text = builder.ToString();
            }

            var raw = builder.ToString().Trim();
            LoggingService.Info("MainWindow.Summarise", $"output {raw.Length} chars: {raw[..Math.Min(raw.Length, 200)]}");

            var (aiEvent, cleanSummary) = Services.Ai.AiActionParser.Parse(raw);
            SummaryText.Text = cleanSummary;

            if (cleanSummary.Length > 0)
            {
                MessageCache.SaveAiSummary(row.AccountId, row.Folder, row.Uid, cleanSummary,
                    aiEvent?.Date.UtcTicks ?? 0, aiEvent?.Title ?? string.Empty);
            }

            if (aiEvent is not null)
            {
                SetCalendarSuggestion(aiEvent);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.Summarise_Click", ex);
            SummaryText.Text = "Couldn't summarise: " + ex.Message;
        }
        finally
        {
            SummariseButton.IsEnabled = true;
            SummariseButtonText.Text = "Summarise";
        }
    }

    private async void SuggestReplies_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentMessage is not { } msg || _vm.CurrentOpenRow is not { } row ||
            !Services.Ai.Ai.Service.IsReady)
        {
            return;
        }

        SuggestRepliesButton.IsEnabled = false;
        SuggestRepliesButtonText.Text = "Drafting…";
        RepliesPanel.Visibility = Visibility.Visible;
        RepliesList.ItemsSource = new List<string> { "…" };

        var prompt = Services.Ai.AiPrompts.SuggestReplies(msg.Subject, msg.FromDisplay, PlainBody(msg));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var raw = await Services.Ai.Ai.Service.CompleteAsync(prompt, cts.Token);
            var replies = Services.Ai.AiReplyParser.Parse(raw);
            LoggingService.Info("MainWindow.SuggestReplies", $"{replies.Count} replies from {raw.Length} chars");

            if (replies.Count > 0)
            {
                MessageCache.SaveAiReplies(row.AccountId, row.Folder, row.Uid, replies);
                RepliesList.ItemsSource = replies;
            }
            else
            {
                RepliesList.ItemsSource = new List<string> { "(no replies generated)" };
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.SuggestReplies_Click", ex);
            RepliesList.ItemsSource = new List<string> { "Couldn't draft replies: " + ex.Message };
        }
        finally
        {
            SuggestRepliesButton.IsEnabled = true;
            SuggestRepliesButtonText.Text = "Suggest replies";
        }
    }

    private void ReplyChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string draft } && draft.Length > 2 && _vm.CurrentMessage is { } msg)
        {
            StartCompose(ComposeMode.Reply, msg, draft);
        }
    }

    private const string EditorPage = """
        <!doctype html><html><head><meta charset="utf-8">
        <style>
          html,body{height:100%;margin:0}
          body{font-family:'Segoe UI',system-ui,sans-serif;font-size:14px;padding:12px;
               box-sizing:border-box;outline:none;overflow-wrap:anywhere}
          img{max-width:100%;height:auto}
          blockquote{border-left:2px solid #ccc;margin:0 0 0 8px;padding-left:10px;color:#777}
          @media (prefers-color-scheme: dark){body{background:#1b1b1b;color:#e6e6e6}}
        </style></head>
        <body contenteditable="true"></body>
        <script>
          function setBody(h){ document.body.innerHTML = h; }
          function getBody(){ return document.body.innerHTML; }
          function exec(c,v){ document.execCommand(c,false,v||null); document.body.focus(); }
          function insertImage(d){ document.execCommand('insertImage',false,d); }
          function insertLink(u){ document.execCommand('createLink',false,u); }
          document.addEventListener('paste', function(e){
            var items=(e.clipboardData||window.clipboardData).items;
            for(var i=0;i<items.length;i++){
              if(items[i].type.indexOf('image')===0){
                var f=items[i].getAsFile(); var r=new FileReader();
                r.onload=function(){ insertImage(r.result); };
                r.readAsDataURL(f); e.preventDefault();
              }
            }
          });
        </script></html>
        """;

    private async Task EnsureComposeEditorAsync()
    {
        try
        {
            await ComposeEditor.EnsureCoreWebView2Async();
            ComposeEditor.CoreWebView2.Settings.AreDevToolsEnabled = false;
            if (!_composeEditorReady)
            {
                ComposeEditor.NavigateToString(EditorPage);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.EnsureComposeEditorAsync", ex);
        }
    }

    private async Task SetComposeHtmlAsync(string html)
    {
        if (!_composeEditorReady)
        {
            _pendingEditorHtml = html;
            return;
        }

        try
        {
            await ComposeEditor.ExecuteScriptAsync($"setBody({JsonSerializer.Serialize(html)})");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.SetComposeHtmlAsync", ex);
        }
    }

    private async Task<string> GetComposeHtmlAsync()
    {
        try
        {
            var raw = await ComposeEditor.ExecuteScriptAsync("getBody()");
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.GetComposeHtmlAsync", ex);
            return string.Empty;
        }
    }

    private static readonly char[] RecipientSeparators = { ',', ';' };

    private void RecipientBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var text = sender.Text ?? string.Empty;
        var sep = text.LastIndexOfAny(RecipientSeparators);
        var token = text[(sep + 1)..].Trim();
        sender.ItemsSource = token.Length >= 2 ? ContactStore.Search(token) : null;
    }

    private void RecipientBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem?.ToString() is not { Length: > 0 } picked)
        {
            return;
        }

        var text = sender.Text ?? string.Empty;
        var sep = text.LastIndexOfAny(RecipientSeparators);
        var prefix = sep >= 0 ? text[..(sep + 1)].TrimEnd() + " " : string.Empty;
        sender.Text = prefix + picked + ", ";
        sender.ItemsSource = null;
    }

    private async void StartCompose(ComposeMode mode, MailMessageContent? source, string? draftText = null)
    {
        var account = _vm.CurrentAccount ?? AccountStore.All.FirstOrDefault();
        if (account is null)
        {
            _ = ShowErrorAsync("No account", "Add an account first.");
            return;
        }

        _composeAccount = account;
        _composeSource = mode == ComposeMode.New ? null : source;
        _composeAttachments.Clear();
        ComposeAttachmentsList.Visibility = Visibility.Collapsed;

        ComposeStatus.IsOpen = false;
        ComposeSendButton.IsEnabled = true;
        ComposePriority.SelectedIndex = 1;
        ComposeTo.Text = ComposeCc.Text = ComposeSubject.Text = string.Empty;

        var signature = BuildSignatureHtml(account.Signature);
        var draft = string.IsNullOrWhiteSpace(draftText)
            ? "<p><br></p>"
            : $"<p>{Esc(draftText).Replace("\r\n", "\n").Replace("\n", "<br>")}</p><p><br></p>";
        var body = draft + signature;
        if (_composeSource is { } src)
        {
            var header = mode == ComposeMode.Forward
                ? $"---------- Forwarded message ----------<br>From: {Esc(src.FromDisplay)}<br>" +
                  $"Date: {Esc(src.Date.LocalDateTime.ToString("f"))}<br>Subject: {Esc(src.Subject)}<br>" +
                  $"To: {Esc(src.ToDisplay)}<br><br>"
                : $"On {Esc(src.Date.LocalDateTime.ToString("f"))}, {Esc(src.FromDisplay)} wrote:<br>";
            var original = src.Html is { Length: > 0 } h ? InnerHtmlOnly(h)
                : $"<pre>{Esc(src.PlainText ?? string.Empty)}</pre>";
            body = $"{draft}{signature}<p><br></p><blockquote>{header}{original}</blockquote>";

            switch (mode)
            {
                case ComposeMode.Reply:
                    ComposeTo.Text = src.ReplyToAddress;
                    ComposeSubject.Text = Prefixed("Re:", src.Subject);
                    break;
                case ComposeMode.ReplyAll:
                    ComposeTo.Text = src.ReplyToAddress;
                    ComposeCc.Text = src.CcDisplay;
                    ComposeSubject.Text = Prefixed("Re:", src.Subject);
                    break;
                case ComposeMode.Forward:
                    ComposeSubject.Text = Prefixed("Fwd:", src.Subject);
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
        await EnsureComposeEditorAsync();
        await SetComposeHtmlAsync(body);
        ComposeTo.Focus(FocusState.Programmatic);
    }

    private async void ComposeCmd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd })
        {
            await ComposeEditor.ExecuteScriptAsync($"exec({JsonSerializer.Serialize(cmd)})");
        }
    }

    private async void ComposeLink_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Header = "Link URL", PlaceholderText = "https://example.com", Width = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Insert link",
            Content = box,
            PrimaryButtonText = "Insert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            await ComposeEditor.ExecuteScriptAsync($"insertLink({JsonSerializer.Serialize(box.Text.Trim())})");
        }
    }

    private async void ComposeInsertImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var buffer = await FileIO.ReadBufferAsync(file);
        var bytes = buffer.ToArray();
        var mime = string.IsNullOrEmpty(file.ContentType) ? "image/png" : file.ContentType;
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        await ComposeEditor.ExecuteScriptAsync($"insertImage({JsonSerializer.Serialize(dataUrl)})");
    }

    private async void ComposeAttach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            _composeAttachments.Add(new OutgoingAttachment
            {
                Name = file.Name,
                Data = buffer.ToArray(),
                ContentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            });
        }

        ComposeAttachmentsList.Visibility = _composeAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ComposeAttachmentRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
        {
            var match = _composeAttachments.FirstOrDefault(a => a.Name == name);
            if (match is not null)
            {
                _composeAttachments.Remove(match);
            }

            ComposeAttachmentsList.Visibility = _composeAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
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

        var cc = ParseAddresses(ComposeCc.Text).ToList();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName, account.Email));
        message.To.AddRange(recipients);
        message.Cc.AddRange(cc);
        message.Subject = ComposeSubject.Text;

        foreach (var box in recipients.Concat(cc))
        {
            ContactStore.Record(box.Address, box.Name);
        }

        var html = await GetComposeHtmlAsync();
        var builder = new BodyBuilder();

        // Pull inline data: images out into linked resources so the sent mail isn't a data-URI blob.
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<img\b[^>]*?\bsrc\s*=\s*""data:(?<mime>[^;]+);base64,(?<data>[^""]+)""[^>]*>",
            m =>
            {
                try
                {
                    var bytes = Convert.FromBase64String(m.Groups["data"].Value);
                    var cid = Guid.NewGuid().ToString("N");
                    var ext = m.Groups["mime"].Value.Split('/').LastOrDefault() ?? "png";
                    var resource = builder.LinkedResources.Add($"{cid}.{ext}", bytes,
                        ContentType.Parse(m.Groups["mime"].Value));
                    resource.ContentId = cid;
                    return $"<img src=\"cid:{cid}\" style=\"max-width:100%\">";
                }
                catch
                {
                    return string.Empty;
                }
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        builder.HtmlBody = $"<html><body>{html}</body></html>";
        builder.TextBody = System.Text.RegularExpressions.Regex.Replace(
            System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")),
            @"\s+\n", "\n").Trim();

        foreach (var attachment in _composeAttachments)
        {
            builder.Attachments.Add(attachment.Name, attachment.Data, ContentType.Parse(attachment.ContentType));
        }

        message.Body = builder.ToMessageBody();

        switch (ComposePriority.SelectedIndex)
        {
            case 2:
                message.Priority = MessagePriority.Urgent;
                message.Importance = MessageImportance.High;
                message.XPriority = XMessagePriority.High;
                break;
            case 0:
                message.Priority = MessagePriority.NonUrgent;
                message.Importance = MessageImportance.Low;
                message.XPriority = XMessagePriority.Low;
                break;
        }

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
            _composeAttachments.Clear();
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

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private static string BuildSignatureHtml(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return string.Empty;
        }

        var lines = Esc(signature).Replace("\r\n", "\n").Replace("\n", "<br>");
        return $"<div class=\"sig\">--<br>{lines}</div>";
    }

    /// Strips the document scaffolding (doctype / html / head / body) so a full email's HTML can be
    /// nested inside a quote block.
    private static string InnerHtmlOnly(string html)
    {
        html = System.Text.RegularExpressions.Regex.Replace(html,
            @"<!doctype[^>]*>|</?html[^>]*>|<head[\s\S]*?</head>|</?body[^>]*>",
            string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return html.Trim();
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
