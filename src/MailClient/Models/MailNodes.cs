using CommunityToolkit.Mvvm.ComponentModel;

namespace MailClient.Models;

/// Left-rail tree item: an account root, or a folder under it. TreeView binds to these via
/// TreeViewNode.Content.
public partial class MailNode : ObservableObject
{
    public required string AccountId { get; init; }

    /// True for the account row, false for a mail folder.
    public required bool IsAccount { get; init; }

    /// IMAP folder full name (e.g. "INBOX", "[Gmail]/Sent Mail"). Empty for an account row.
    public string FolderFullName { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    // Segoe Fluent Icons codepoints.
    public string Glyph => IsAccount ? "" : FolderGlyph();

    private string FolderGlyph() => DisplayName.ToLowerInvariant() switch
    {
        "inbox" => "",
        "sent" or "sent mail" or "sent items" => "",
        "drafts" => "",
        "trash" or "bin" or "deleted" or "deleted items" => "",
        "spam" or "junk" or "junk email" => "",
        "archive" or "all mail" => "",
        _ => "",
    };
}
