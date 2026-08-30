namespace MailClient.Models;

/// A user calendar entry, persisted locally (calendar-events.json).
public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public string TimeDisplay => Date.LocalDateTime.ToString("d MMM yyyy");
}
