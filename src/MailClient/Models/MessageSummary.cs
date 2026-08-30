using CommunityToolkit.Mvvm.ComponentModel;

namespace MailClient.Models;

/// One row in the message list. Cached in SQLite so a folder opens instantly, then refreshed.
public partial class MessageRow : ObservableObject
{
    public required string AccountId { get; init; }
    public required string Folder { get; init; }
    public required uint Uid { get; init; }

    public string From { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public bool HasAttachments { get; init; }

    /// 2 = high, 1 = normal, 0 = low.
    public int Priority { get; init; } = 1;

    public bool IsHighPriority => Priority >= 2;
    public bool IsLowPriority => Priority <= 0;

    [ObservableProperty]
    public partial bool IsRead { get; set; }

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty]
    public partial bool IsFlagged { get; set; }

    public string DateDisplay =>
        Date.LocalDateTime.Date == DateTime.Today ? Date.LocalDateTime.ToString("t")
        : Date.LocalDateTime.Year == DateTime.Today.Year ? Date.LocalDateTime.ToString("d MMM")
        : Date.LocalDateTime.ToString("d MMM yyyy");

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject;
}
