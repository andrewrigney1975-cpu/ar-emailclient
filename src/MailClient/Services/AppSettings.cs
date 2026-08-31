namespace MailClient.Services;

/// User-interface preferences persisted across sessions (settings.json).
public sealed class AppSettingsData
{
    /// Whether the right-rail calendar pane is shown.
    public bool CalendarVisible { get; set; }

    /// Detailed calendar view: Month / Week / WorkWeek / ThreeDay / Day.
    public string CalendarViewMode { get; set; } = "Week";

    /// Last folder the user had open, reopened on the next launch.
    public string LastAccountId { get; set; } = string.Empty;
    public string LastFolder { get; set; } = string.Empty;
    public string LastFolderTitle { get; set; } = string.Empty;

    /// Pane widths in pixels (0 = use the built-in default).
    public double RailWidth { get; set; }
    public double ListWidth { get; set; }

    /// Calendar-event ids we've already shown a "1 day away" reminder toast for.
    public List<string> NotifiedReminderIds { get; set; } = new();

    /// Date-group headers ("Today", "Last Week", …) the user has collapsed in the message list.
    public List<string> CollapsedDateGroups { get; set; } = new();

    /// On-device AI. Off until the user opts in and a model has downloaded.
    public bool AiEnabled { get; set; }
    public string AiModelId { get; set; } = "phi-3.5-mini-int4";

    /// Cached AI briefings (regenerated when the stored date is stale).
    public string BriefTodayDate { get; set; } = string.Empty;
    public string BriefTodayText { get; set; } = string.Empty;
    public string BriefWeekDate { get; set; } = string.Empty;
    public string BriefWeekText { get; set; } = string.Empty;
}

public static class AppSettings
{
    private static readonly JsonFileStore<AppSettingsData> Store = new("settings.json", () => new AppSettingsData());

    public static AppSettingsData Current => Store.Load();

    public static void Update(Action<AppSettingsData> mutate)
    {
        var settings = Store.Load();
        mutate(settings);
        Store.Save(settings);
    }
}
