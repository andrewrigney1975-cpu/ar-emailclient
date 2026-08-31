using System.Text;
using MailClient.Services;

namespace MailClient.Services.Ai;

/// Gathers the raw material (calendar, follow-ups, priority mail, recent mail) that the model
/// turns into a "Today" brief or a weekly digest.
public static class BriefingBuilder
{
    private static string FirstLine(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : s.Split('\n', '\r').FirstOrDefault()?.Trim() ?? string.Empty;

    public static string Today()
    {
        var today = DateTime.Today;
        var sb = new StringBuilder();
        sb.AppendLine($"Date: {today:dddd d MMMM yyyy}").AppendLine();

        var events = CalendarStore.All
            .Where(e => !e.Done && e.Date.LocalDateTime.Date == today)
            .OrderBy(e => e.Date)
            .ToList();
        sb.AppendLine("Calendar today:");
        sb.AppendLine(events.Count == 0 ? "- none" : string.Join("\n", events.Select(e => $"- {e.Title}")));
        sb.AppendLine();

        var followUps = MessageCache.LoadFollowUps()
            .Where(r => true)
            .Take(12)
            .ToList();
        sb.AppendLine("Emails to follow up:");
        sb.AppendLine(followUps.Count == 0
            ? "- none"
            : string.Join("\n", followUps.Select(r => $"- {r.Subject} (from {r.From})")));
        sb.AppendLine();

        var unread = MessageCache.LoadUnread(200);
        var highPriority = unread.Where(r => r.IsHighPriority).Take(10).ToList();
        sb.AppendLine("Unread, high priority:");
        sb.AppendLine(highPriority.Count == 0
            ? "- none"
            : string.Join("\n", highPriority.Select(r => $"- {r.Subject} (from {r.From})")));
        sb.AppendLine();

        sb.AppendLine($"Unread total: {unread.Count}");
        return sb.ToString();
    }

    public static string Week()
    {
        var items = MessageCache.RecentForBriefing(7);
        if (items.Count == 0)
        {
            return "No emails in the last 7 days.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Emails in the last 7 days (newest first):").AppendLine();
        foreach (var item in items.Take(70))
        {
            var flag = item.Priority >= 2 ? "[!] " : string.Empty;
            var note = FirstLine(item.Summary);
            sb.Append($"- [{item.Date.LocalDateTime:ddd}] {flag}{item.From}: {item.Subject}");
            if (note.Length > 0)
            {
                sb.Append("  — ").Append(note);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
