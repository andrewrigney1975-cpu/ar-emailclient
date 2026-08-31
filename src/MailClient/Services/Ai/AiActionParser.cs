using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailClient.Services; // CalendarSuggestion (DateActionScanner.cs)

namespace MailClient.Services.Ai;

/// Pulls the "EVENT: { ... }" line the Summarise prompt emits and turns it into a calendar
/// suggestion. Returns null when the model said "EVENT: none" or the line is unusable.
public static class AiActionParser
{
    private sealed record EventJson(string? Date, string? Title, string? Amount);

    private static readonly Regex Line = new(@"EVENT:\s*(?<val>\{.*\}|none)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// (suggestion, summaryWithoutEventLine)
    public static (CalendarSuggestion? Suggestion, string CleanSummary) Parse(string modelOutput)
    {
        var match = Line.Match(modelOutput);
        var clean = (match.Success ? modelOutput.Remove(match.Index, match.Length) : modelOutput).Trim();

        if (!match.Success)
        {
            return (null, clean);
        }

        var value = match.Groups["val"].Value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return (null, clean);
        }

        try
        {
            var e = JsonSerializer.Deserialize<EventJson>(value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (e?.Date is not { Length: > 0 } dateText ||
                !DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return (null, clean);
            }

            var title = string.IsNullOrWhiteSpace(e.Title) ? "Follow up" : e.Title!.Trim();
            if (!string.IsNullOrWhiteSpace(e.Amount) && !title.Contains(e.Amount!, StringComparison.OrdinalIgnoreCase))
            {
                title = $"{title} ({e.Amount!.Trim()})";
            }

            var when = new DateTimeOffset(new DateTime(date.Year, date.Month, date.Day, 9, 0, 0, DateTimeKind.Local));
            return (new CalendarSuggestion(when, title), clean);
        }
        catch (JsonException)
        {
            return (null, clean);
        }
    }
}
