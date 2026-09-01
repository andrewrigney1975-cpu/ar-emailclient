using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MailClient.Models;

/// A typed value on a contact (email / phone / address). Type is a free-ish label so the set can
/// be extended without a schema change; common ones are offered in the editor.
public sealed partial class ContactField : ObservableObject
{
    [ObservableProperty]
    public partial string Type { get; set; } = "personal";

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

public sealed partial class Contact : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string FirstName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string LastName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Nickname { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Company { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? DateOfBirth { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    /// Photo as a base64-encoded image (jpg/png), or "" for none.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    public partial string PhotoBase64 { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    public ObservableCollection<ContactField> Emails { get; set; } = new();
    public ObservableCollection<ContactField> Phones { get; set; } = new();
    public ObservableCollection<ContactField> Addresses { get; set; } = new();

    /// Names of the groups this contact belongs to.
    public ObservableCollection<string> Groups { get; set; } = new();
    public ObservableCollection<string> Tags { get; set; } = new();

    public string DisplayName
    {
        get
        {
            var n = $"{FirstName} {LastName}".Trim();
            return n.Length > 0 ? n : Nickname.Length > 0 ? Nickname
                : Emails.FirstOrDefault()?.Value ?? "(no name)";
        }
    }

    public string Initials
    {
        get
        {
            var f = FirstName.Trim();
            var l = LastName.Trim();
            var s = (f.Length > 0 ? f[0].ToString() : string.Empty) + (l.Length > 0 ? l[0].ToString() : string.Empty);
            return s.Length > 0 ? s.ToUpperInvariant() : "?";
        }
    }

    public string PrimaryEmail => Emails.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value))?.Value ?? string.Empty;
    public string PrimaryPhone => Phones.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Value))?.Value ?? string.Empty;
    public bool HasPhoto => PhotoBase64.Length > 0;
    public string SortKey => string.IsNullOrWhiteSpace(LastName) ? DisplayName : $"{LastName} {FirstName}".Trim();
}

public sealed partial class ContactGroup : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

}
