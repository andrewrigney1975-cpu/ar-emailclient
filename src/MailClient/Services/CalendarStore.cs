using MailClient.Models;

namespace MailClient.Services;

/// Local calendar events (calendar-events.json).
public static class CalendarStore
{
    private static readonly JsonFileStore<List<CalendarEvent>> Store =
        new("calendar-events.json", () => new List<CalendarEvent>());

    public static event EventHandler? Changed;

    public static IReadOnlyList<CalendarEvent> All => Store.Load();

    public static List<CalendarEvent> ForDay(DateTimeOffset day)
    {
        var d = day.LocalDateTime.Date;
        return Store.Load()
            .Where(e => e.Date.LocalDateTime.Date == d)
            .OrderBy(e => e.Date)
            .ToList();
    }

    public static bool AnyOn(DateTimeOffset day)
    {
        var d = day.LocalDateTime.Date;
        return Store.Load().Any(e => e.Date.LocalDateTime.Date == d);
    }

    public static void Add(CalendarEvent entry)
    {
        var list = Store.Load();
        list.Add(entry);
        Store.Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(string id)
    {
        var list = Store.Load();
        if (list.RemoveAll(e => e.Id == id) > 0)
        {
            Store.Save(list);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static CalendarEvent? Find(string id) => Store.Load().FirstOrDefault(e => e.Id == id);

    public static void SetDone(string id, bool done)
    {
        var list = Store.Load();
        var entry = list.FirstOrDefault(e => e.Id == id);
        if (entry is not null && entry.Done != done)
        {
            entry.Done = done;
            Store.Save(list);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
