using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MailClient.Models;

public enum MailListKind
{
    DateGroup,
    Thread,
    Message,
}

/// One node in the message-list TreeView. A date group contains threads and/or single messages;
/// a thread contains messages; a message is a leaf.
public partial class MailListNode : ObservableObject
{
    public required MailListKind Kind { get; init; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public ObservableCollection<MailListNode> Children { get; } = new();

    /// Header text for a date group or thread row.
    public string Header { get; init; } = string.Empty;

    /// Number of messages under a date group / thread.
    public int MessageCount { get; init; }

    /// The message for a Kind == Message leaf.
    public MessageRow? Row { get; init; }

    public bool IsHeader => Kind != MailListKind.Message;
    public bool IsMessage => Kind == MailListKind.Message;
    public bool IsThread => Kind == MailListKind.Thread;

    public string CountLabel => MessageCount.ToString();
}
