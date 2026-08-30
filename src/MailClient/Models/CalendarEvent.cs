namespace MailClient.Models;

/// A user calendar entry, persisted locally (calendar-events.json).
public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Done { get; set; }

    /// If this event was created from a message follow-up flag, the message it links back to.
    public string SourceAccountId { get; set; } = string.Empty;
    public string SourceFolder { get; set; } = string.Empty;
    public uint SourceUid { get; set; }

    public string TimeDisplay => Date.LocalDateTime.ToString("d MMM yyyy");

    public string DayDisplay => Date.LocalDateTime.ToString("ddd d");
}

/// A month's worth of upcoming calendar events, for the grouped "Upcoming" list.
public sealed class CalendarMonthGroup
{
    public required string Header { get; init; }
    public required IReadOnlyList<CalendarEvent> Events { get; init; }
}
