namespace MailClient.Services;

/// User-interface preferences persisted across sessions (settings.json).
public sealed class AppSettingsData
{
    /// Whether the right-rail calendar pane is shown.
    public bool CalendarVisible { get; set; }

    /// Last folder the user had open, reopened on the next launch.
    public string LastAccountId { get; set; } = string.Empty;
    public string LastFolder { get; set; } = string.Empty;
    public string LastFolderTitle { get; set; } = string.Empty;
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
