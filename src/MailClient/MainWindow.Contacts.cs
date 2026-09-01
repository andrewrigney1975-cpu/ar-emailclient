using System.Runtime.InteropServices.WindowsRuntime;
using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MailClient;

public sealed partial class MainWindow
{
    private sealed class ContactNavItem
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Glyph { get; init; } = string.Empty;
        public bool CanFavourite { get; init; }
        public bool IsFavourite { get; init; }
    }

    private static readonly string[] EmailTypes = { "personal", "work", "other" };
    private static readonly string[] PhoneTypes =
        { "personal mobile", "personal landline", "work mobile", "work landline", "fax", "other" };
    private static readonly string[] AddressTypes = { "home", "work", "postal", "other" };

    private bool _contactsMode;
    private bool _contactsHooked;
    private string _contactFilterKey = "all";
    private string _contactView = "list";
    private bool _contactSortByGroup;
    private string _contactSearch = string.Empty;
    private Contact? _editingContact;

    private void ContactsMode_Click(object sender, RoutedEventArgs e) => SetContactsMode(!_contactsMode);

    private void SetContactsMode(bool on)
    {
        if (on && _calendarMode)
        {
            SetCalendarMode(false);
        }

        _contactsMode = on;

        MailRail.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        MailListPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ReadingPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ContactsRail.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ContactEditorPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ContactListPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (!on)
        {
            return;
        }

        if (!_contactsHooked)
        {
            _contactsHooked = true;
            AddressBook.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_contactsMode)
                {
                    RefreshGroupNav();
                    RenderContactList();
                }
            });
        }

        RefreshGroupNav();
        RenderContactList();
        if (_editingContact is null)
        {
            NewContactEditor();
        }
    }

    // ----- groups rail -----

    private void RefreshGroupNav()
    {
        var items = new List<ContactNavItem>
        {
            new() { Key = "all", Label = "All contacts", Glyph = "" },
            new() { Key = "fav", Label = "Favourites", Glyph = "" },
        };

        foreach (var g in AddressBook.Groups.OrderByDescending(g => g.IsFavourite).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new ContactNavItem
            {
                Key = "group:" + g.Name,
                Label = g.Name,
                Glyph = "",
                CanFavourite = true,
                IsFavourite = g.IsFavourite,
            });
        }

        GroupList.SelectionChanged -= GroupList_SelectionChanged;
        GroupList.ItemsSource = items;
        GroupList.SelectedItem = items.FirstOrDefault(i => i.Key == _contactFilterKey) ?? items[0];
        GroupList.SelectionChanged += GroupList_SelectionChanged;

        var isGroup = _contactFilterKey.StartsWith("group:", StringComparison.Ordinal);
        RenameGroupButton.Visibility = DeleteGroupButton.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not ContactNavItem item)
        {
            return;
        }

        _contactFilterKey = item.Key;
        ContactListTitle.Text = item.Label;
        var isGroup = item.Key.StartsWith("group:", StringComparison.Ordinal);
        RenameGroupButton.Visibility = DeleteGroupButton.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        RenderContactList();
    }

    private void GroupFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && key.StartsWith("group:", StringComparison.Ordinal))
        {
            var name = key["group:".Length..];
            var current = AddressBook.Groups.FirstOrDefault(g => g.Name == name)?.IsFavourite ?? false;
            AddressBook.SetGroupFavourite(name, !current);
        }
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("New group", "Group name", string.Empty, "Create");
        if (name is not null)
        {
            AddressBook.AddGroup(name);
        }
    }

    private async void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_contactFilterKey.StartsWith("group:", StringComparison.Ordinal))
        {
            return;
        }

        var old = _contactFilterKey["group:".Length..];
        var name = await PromptTextAsync("Rename group", "New name", old, "Rename");
        if (name is not null && name != old)
        {
            _contactFilterKey = "group:" + name;
            AddressBook.RenameGroup(old, name);
        }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_contactFilterKey.StartsWith("group:", StringComparison.Ordinal))
        {
            return;
        }

        var name = _contactFilterKey["group:".Length..];
        var confirm = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete group",
            Content = new TextBlock
            {
                Text = $"Delete the group “{name}”? Contacts are kept but lose this group.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (confirm == ContentDialogResult.Primary)
        {
            _contactFilterKey = "all";
            AddressBook.DeleteGroup(name);
        }
    }

    // ----- contact list -----

    private void ContactView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string view })
        {
            _contactView = view;
        }

        ViewListButton.IsChecked = _contactView == "list";
        ViewTilesButton.IsChecked = _contactView == "tiles";
        ViewPhotosButton.IsChecked = _contactView == "photos";
        RenderContactList();
    }

    private void ContactSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        _contactSortByGroup = ContactSortBox.SelectedIndex == 1;
        RenderContactList();
    }

    private void ContactSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _contactSearch = (sender.Text ?? string.Empty).Trim();
        RenderContactList();
    }

    private void RenderContactList()
    {
        IEnumerable<Contact> items = AddressBook.Contacts;

        items = _contactFilterKey switch
        {
            "fav" => items.Where(c => c.IsFavourite),
            { } k when k.StartsWith("group:", StringComparison.Ordinal) =>
                items.Where(c => c.Groups.Any(g => g.Equals(k["group:".Length..], StringComparison.OrdinalIgnoreCase))),
            _ => items,
        };

        if (_contactSearch.Length > 0)
        {
            var q = _contactSearch;
            items = items.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Company.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Emails.Any(x => x.Value.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                c.Phones.Any(x => x.Value.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                c.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        var ordered = _contactSortByGroup
            ? items.OrderByDescending(c => c.IsFavourite)
                   .ThenBy(c => c.Groups.FirstOrDefault() ?? "￿", StringComparer.OrdinalIgnoreCase)
                   .ThenBy(c => c.SortKey, StringComparer.OrdinalIgnoreCase)
            : items.OrderByDescending(c => c.IsFavourite)
                   .ThenBy(c => c.SortKey, StringComparer.OrdinalIgnoreCase);

        var list = ordered.ToList();

        string template;
        Microsoft.UI.Xaml.Controls.Layout layout;
        switch (_contactView)
        {
            case "tiles":
                template = "ContactTileTemplate";
                layout = new UniformGridLayout { MinItemWidth = 240, MinItemHeight = 88, MinRowSpacing = 4, MinColumnSpacing = 4 };
                break;
            case "photos":
                template = "ContactPhotoTemplate";
                layout = new UniformGridLayout { MinItemWidth = 132, MinItemHeight = 150, MinRowSpacing = 8, MinColumnSpacing = 8 };
                break;
            default:
                template = "ContactRowTemplate";
                layout = new StackLayout { Spacing = 2 };
                break;
        }

        ContactRepeater.Layout = layout;
        ContactRepeater.ItemTemplate = (DataTemplate)RootGrid.Resources[template];
        ContactRepeater.ItemsSource = list;

        ContactListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContactCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && AddressBook.Find(id) is { } c)
        {
            LoadContactEditor(c);
        }
    }

    private void ContactFavourite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && AddressBook.Find(id) is { } c)
        {
            AddressBook.SetFavourite(id, !c.IsFavourite);
        }
    }

    // ----- editor -----

    private static Contact Clone(Contact c) => new()
    {
        Id = c.Id,
        FirstName = c.FirstName,
        LastName = c.LastName,
        Nickname = c.Nickname,
        Company = c.Company,
        DateOfBirth = c.DateOfBirth,
        Notes = c.Notes,
        PhotoBase64 = c.PhotoBase64,
        IsFavourite = c.IsFavourite,
        Emails = new(c.Emails.Select(f => new ContactField { Type = f.Type, Value = f.Value })),
        Phones = new(c.Phones.Select(f => new ContactField { Type = f.Type, Value = f.Value })),
        Addresses = new(c.Addresses.Select(f => new ContactField { Type = f.Type, Value = f.Value })),
        Groups = new(c.Groups),
        Tags = new(c.Tags),
    };

    private void NewContactEditor()
    {
        _editingContact = new Contact();
        _editingContact.Emails.Add(new ContactField { Type = "personal" });
        LoadContactEditorFields(isNew: true);
    }

    private void LoadContactEditor(Contact c)
    {
        _editingContact = Clone(c);
        LoadContactEditorFields(isNew: false);
    }

    private void LoadContactEditorFields(bool isNew)
    {
        var c = _editingContact!;
        ContactEditorHeading.Text = isNew ? "New contact" : "Edit contact";
        ContactFirst.Text = c.FirstName;
        ContactLast.Text = c.LastName;
        ContactNick.Text = c.Nickname;
        ContactCompany.Text = c.Company;
        ContactDob.Date = c.DateOfBirth;
        ContactTags.Text = string.Join(", ", c.Tags);
        ContactNotes.Text = c.Notes;

        ContactPhotoInitials.Text = c.Initials;
        ContactPhotoBrush.ImageSource = PhotoSource(c.PhotoBase64);

        RenderFieldPanel(ContactEmailsPanel, c.Emails, EmailTypes, multiline: false);
        RenderFieldPanel(ContactPhonesPanel, c.Phones, PhoneTypes, multiline: false);
        RenderFieldPanel(ContactAddressesPanel, c.Addresses, AddressTypes, multiline: true);

        ContactGroupsPanel.Children.Clear();
        foreach (var g in AddressBook.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            ContactGroupsPanel.Children.Add(new CheckBox
            {
                Content = g.Name,
                IsChecked = c.Groups.Any(x => x.Equals(g.Name, StringComparison.OrdinalIgnoreCase)),
                Tag = g.Name,
            });
        }

        if (AddressBook.Groups.Count == 0)
        {
            ContactGroupsPanel.Children.Add(new TextBlock
            {
                Text = "No groups yet — create one in the left rail.", Opacity = 0.6, FontSize = 12,
            });
        }

        ContactDeleteButton.Visibility = ContactEmailButton.Visibility =
            isNew ? Visibility.Collapsed : Visibility.Visible;
    }

    private static BitmapImage? PhotoSource(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(bytes.AsBuffer()).AsTask().Wait();
            stream.Seek(0);
            image.SetSource(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void RenderFieldPanel(StackPanel panel, IList<ContactField> fields, string[] types, bool multiline)
    {
        panel.Children.Clear();
        foreach (var field in fields)
        {
            var captured = field;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var typeBox = new ComboBox
            {
                IsEditable = true,
                ItemsSource = types,
                Text = string.IsNullOrWhiteSpace(field.Type) ? types[0] : field.Type,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            typeBox.TextSubmitted += (s, _) => captured.Type = s.Text;
            typeBox.SelectionChanged += (_, _) => { if (typeBox.SelectedItem is string t) captured.Type = t; };
            Grid.SetColumn(typeBox, 0);

            var valueBox = new TextBox
            {
                Text = field.Value,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
            valueBox.TextChanged += (_, _) => captured.Value = valueBox.Text;
            Grid.SetColumn(valueBox, 1);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                fields.Remove(captured);
                RenderFieldPanel(panel, fields, types, multiline);
            };
            Grid.SetColumn(remove, 2);

            row.Children.Add(typeBox);
            row.Children.Add(valueBox);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
    }

    private void ContactAddEmail_Click(object sender, RoutedEventArgs e)
    {
        _editingContact!.Emails.Add(new ContactField { Type = "personal" });
        RenderFieldPanel(ContactEmailsPanel, _editingContact.Emails, EmailTypes, multiline: false);
    }

    private void ContactAddPhone_Click(object sender, RoutedEventArgs e)
    {
        _editingContact!.Phones.Add(new ContactField { Type = "personal mobile" });
        RenderFieldPanel(ContactPhonesPanel, _editingContact.Phones, PhoneTypes, multiline: false);
    }

    private void ContactAddAddress_Click(object sender, RoutedEventArgs e)
    {
        _editingContact!.Addresses.Add(new ContactField { Type = "home" });
        RenderFieldPanel(ContactAddressesPanel, _editingContact.Addresses, AddressTypes, multiline: true);
    }

    private void ContactNew_Click(object sender, RoutedEventArgs e) => NewContactEditor();

    private void ContactSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editingContact is not { } c)
        {
            return;
        }

        c.FirstName = ContactFirst.Text.Trim();
        c.LastName = ContactLast.Text.Trim();
        c.Nickname = ContactNick.Text.Trim();
        c.Company = ContactCompany.Text.Trim();
        c.DateOfBirth = ContactDob.Date;
        c.Notes = ContactNotes.Text.Trim();
        c.Tags = new(ContactTags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        c.Emails = new(c.Emails.Where(f => !string.IsNullOrWhiteSpace(f.Value)));
        c.Phones = new(c.Phones.Where(f => !string.IsNullOrWhiteSpace(f.Value)));
        c.Addresses = new(c.Addresses.Where(f => !string.IsNullOrWhiteSpace(f.Value)));

        c.Groups.Clear();
        foreach (var child in ContactGroupsPanel.Children)
        {
            if (child is CheckBox { IsChecked: true, Tag: string g })
            {
                c.Groups.Add(g);
            }
        }

        AddressBook.Upsert(c);
        RefreshGroupNav();
        RenderContactList();
        LoadContactEditor(c);
    }

    private async void ContactDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingContact is not { } c)
        {
            return;
        }

        var confirm = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete contact",
            Content = new TextBlock { Text = $"Delete “{c.DisplayName}”?", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (confirm == ContentDialogResult.Primary)
        {
            AddressBook.Delete(c.Id);
            NewContactEditor();
        }
    }

    private void ContactEmail_Click(object sender, RoutedEventArgs e)
    {
        if (_editingContact?.PrimaryEmail is { Length: > 0 } addr)
        {
            SetContactsMode(false);
            _contactsMode = false;
            StartCompose(ComposeMode.New, null);
            ComposeTo.Text = _editingContact.DisplayName is { Length: > 0 } n && n != "(no name)"
                ? $"{n} <{addr}>, "
                : addr + ", ";
        }
    }

    private async void ContactChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_editingContact is null)
        {
            return;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var bytes = (await Windows.Storage.FileIO.ReadBufferAsync(file)).ToArray();
        if (bytes.Length > 3_000_000)
        {
            await ShowErrorAsync("Image too large", "Please choose an image under 3 MB.");
            return;
        }

        _editingContact.PhotoBase64 = Convert.ToBase64String(bytes);
        ContactPhotoBrush.ImageSource = PhotoSource(_editingContact.PhotoBase64);
    }

    private void ContactRemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_editingContact is not null)
        {
            _editingContact.PhotoBase64 = string.Empty;
            ContactPhotoBrush.ImageSource = null;
        }
    }

    private async void ContactImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".csv", ".json", ".html", ".htm", ".txt" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var parsed = await Task.Run(() => ContactImporter.Parse(file.Path));
            if (parsed.Count == 0)
            {
                await ShowErrorAsync("Nothing to import", "Couldn't find any contacts in that file.");
                return;
            }

            var added = ContactImporter.Merge(parsed);
            RefreshGroupNav();
            RenderContactList();

            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Import complete",
                Content = new TextBlock
                {
                    Text = $"Added {added} contact(s)." +
                           (added < parsed.Count ? $" Skipped {parsed.Count - added} already in your address book." : string.Empty),
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.ContactImport_Click", ex);
            await ShowErrorAsync("Import failed", ex.Message);
        }
    }
}
