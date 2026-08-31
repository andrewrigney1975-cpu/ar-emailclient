namespace MailClient.Models;

/// A user calendar entry, persisted locally (calendar-events.json).
public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// Start (local). For all-day events only the date part matters.
    public DateTimeOffset Date { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public bool AllDay { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Done { get; set; }

    /// If this event was created from a message follow-up flag, the message it links back to.
    public string SourceAccountId { get; set; } = string.Empty;
    public string SourceFolder { get; set; } = string.Empty;
    public uint SourceUid { get; set; }

    public DateTime StartLocal => Date.LocalDateTime;
    public DateTime EndLocal => AllDay ? Date.LocalDateTime.Date.AddDays(1) : Date.LocalDateTime.AddMinutes(DurationMinutes);

    public string TimeDisplay => Date.LocalDateTime.ToString("d MMM yyyy");

    public string DayDisplay => Date.LocalDateTime.ToString("ddd d");

    public string TimeRangeDisplay => AllDay
        ? "All day"
        : $"{Date.LocalDateTime:h:mm tt} – {EndLocal:h:mm tt}";
}

/// A month's worth of upcoming calendar events, for the grouped "Upcoming" list.
public sealed class CalendarMonthGroup
{
    public required string Header { get; init; }
    public required IReadOnlyList<CalendarEvent> Events { get; init; }
}
