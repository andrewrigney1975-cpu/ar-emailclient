namespace MailClient.Models;

public sealed record MailAttachmentInfo(string FileName, long Size, int Index);

/// A fully-fetched message for the reading pane.
public sealed class MailMessageContent
{
    public string Subject { get; init; } = string.Empty;
    public string FromDisplay { get; init; } = string.Empty;
    public string ToDisplay { get; init; } = string.Empty;
    public string CcDisplay { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }

    /// Sanitised HTML ready to hand to a WebView2 (remote images already neutralised unless the
    /// user opts in), or null when the message has only a plain-text body.
    public string? Html { get; init; }

    public string? PlainText { get; init; }

    /// The message references remote images/CSS.
    public bool HadRemoteContent { get; init; }

    /// Remote content was actually loaded for this render (user opted in, or the sender's
    /// domain is on the always-allow list).
    public bool RemoteContentAllowed { get; init; }

    public IReadOnlyList<MailAttachmentInfo> Attachments { get; init; } = Array.Empty<MailAttachmentInfo>();

    // Kept so "Reply" / "Forward" can quote and thread correctly.
    public string MessageId { get; init; } = string.Empty;
    public string References { get; init; } = string.Empty;
    public string ReplyToAddress { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
}
