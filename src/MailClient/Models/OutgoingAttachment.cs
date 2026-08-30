namespace MailClient.Models;

/// A file the user has attached to the message currently being composed.
public sealed class OutgoingAttachment
{
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
}
