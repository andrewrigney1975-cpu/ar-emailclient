using System.Globalization;
using System.Text.RegularExpressions;

namespace MailClient.Services;

public sealed record CalendarSuggestion(DateTimeOffset Date, string Title);

/// Best-effort scan of a message for a due date / actionable deadline, plus a guessed title
/// ("Pay Electricity Bill", "Pay Car Registration", …) for a calendar entry.
public static class DateActionScanner
{
    // en-AU: day comes first in numeric dates.
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    /// Phrases that mark a nearby date as the one that matters. A date is only taken if one of
    /// these ends within a short window before it.
    private static readonly string[] Cues =
    {
        "amount due", "balance due", "payment due", "total due", "now due", "due date", "due on",
        "due by", "due ", "due:", "is due", "overdue", "payable", "pay by", "pay before",
        "please pay", "to be paid", "bill by",
        "debited on", "debited automatically on", "will be debited", "direct debit", "debit date",
        "charged on", "auto-pay", "automatic payment", "scheduled for",
        "on or before", "no later than", "by the", "expires", "expiry", "expiry date",
        "renew by", "renewal date", "valid until", "rsvp by", "respond by", "reply by",
        "closes on", "closing date", "deadline", "final date", "final payment", "last day",
    };

    private const int CueWindow = 140;

    private const string MonthNames =
        "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|" +
        "sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    private static readonly Regex StyleOrScript =
        new(@"<(style|script)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex IsoDate = new(@"\b(\d{4})-(\d{2})-(\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex NumericDate = new(@"\b(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})\b", RegexOptions.Compiled);
    private static readonly Regex DayMonthDate = new(
        $@"\b(\d{{1,2}})(?:st|nd|rd|th)?\s+({MonthNames})\b(?:\s+(\d{{4}}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MonthDayDate = new(
        $@"\b({MonthNames})\s+(\d{{1,2}})(?:st|nd|rd|th)?\b(?:,?\s+(\d{{4}}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CalendarSuggestion? Scan(string? subject, string? body, string? fromAddress)
    {
        var text = Normalise(subject, body);
        if (text.Length == 0)
        {
            return null;
        }

        var date = FindCuedDate(text);
        if (date is null)
        {
            return null;
        }

        return new CalendarSuggestion(date.Value, InferTitle(text.ToLowerInvariant(), subject, fromAddress));
    }

    private static string Normalise(string? subject, string? body)
    {
        var raw = (subject ?? string.Empty) + "\n" + (body ?? string.Empty);
        raw = StyleOrScript.Replace(raw, " ");
        raw = Tags.Replace(raw, " ");
        raw = System.Net.WebUtility.HtmlDecode(raw);
        raw = raw.Replace(' ', ' ');
        raw = Whitespace.Replace(raw, " ");
        return raw.Trim();
    }

    private static DateTimeOffset? FindCuedDate(string text)
    {
        var lower = text.ToLowerInvariant();

        var cueEnds = new List<int>();
        foreach (var cue in Cues)
        {
            for (var i = lower.IndexOf(cue, StringComparison.Ordinal); i >= 0;
                 i = lower.IndexOf(cue, i + cue.Length, StringComparison.Ordinal))
            {
                cueEnds.Add(i + cue.Length);
            }
        }

        if (cueEnds.Count == 0)
        {
            return null;
        }

        (DateTimeOffset Date, int Distance)? best = null;
        foreach (var (date, pos) in AllDates(text))
        {
            var distance = int.MaxValue;
            foreach (var end in cueEnds)
            {
                if (end <= pos + 2 && pos - end < distance)
                {
                    distance = pos - end;
                }
            }

            if (distance <= CueWindow && (best is null || distance < best.Value.Distance))
            {
                best = (date, distance);
            }
        }

        return best?.Date;
    }

    private static IEnumerable<(DateTimeOffset Date, int Pos)> AllDates(string text)
    {
        foreach (Match m in IsoDate.Matches(text))
        {
            if (TryMake(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), out var d))
            {
                yield return (d, m.Index);
            }
        }

        foreach (Match m in NumericDate.Matches(text))
        {
            var year = int.Parse(m.Groups[3].Value);
            if (year < 100)
            {
                year += 2000;
            }

            if (TryMake(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), year, out var d))
            {
                yield return (d, m.Index);
            }
        }

        foreach (Match m in DayMonthDate.Matches(text))
        {
            var day = int.Parse(m.Groups[1].Value);
            if (TryMonth(m.Groups[2].Value, out var month) &&
                TryMake(day, month, YearOrGuess(m.Groups[3].Value, month, day), out var d))
            {
                yield return (d, m.Index);
            }
        }

        foreach (Match m in MonthDayDate.Matches(text))
        {
            var day = int.Parse(m.Groups[2].Value);
            if (TryMonth(m.Groups[1].Value, out var month) &&
                TryMake(day, month, YearOrGuess(m.Groups[3].Value, month, day), out var d))
            {
                yield return (d, m.Index);
            }
        }
    }

    private static int YearOrGuess(string captured, int month, int day)
    {
        if (int.TryParse(captured, out var y) && y > 1900)
        {
            return y;
        }

        var today = DateTime.Today;
        var candidate = new DateTime(today.Year, Math.Clamp(month, 1, 12), Math.Min(day, 28));
        return candidate < today.AddDays(-14) ? today.Year + 1 : today.Year;
    }

    private static bool TryMake(int day, int month, int year, out DateTimeOffset value)
    {
        value = default;
        if (month is < 1 or > 12 || day < 1 || year is < 2000 or > 2100 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        value = new DateTimeOffset(new DateTime(year, month, day, 9, 0, 0, DateTimeKind.Local));
        return true;
    }

    private static bool TryMonth(string token, out int month)
    {
        month = 0;
        var t = token.Trim().ToLowerInvariant();
        for (var i = 1; i <= 12; i++)
        {
            var name = Au.DateTimeFormat.GetMonthName(i).ToLowerInvariant();
            if (t == name || (t.Length >= 3 && name.StartsWith(t, StringComparison.Ordinal)))
            {
                month = i;
                return true;
            }
        }

        return false;
    }

    private static string InferTitle(string lower, string? subject, string? fromAddress)
    {
        (string[] keys, string title)[] rules =
        {
            (new[] { "electricity", "power bill", "energy", "kwh", "agl", "origin energy", "energyaustralia", "red energy" }, "Pay Electricity Bill"),
            (new[] { "gas bill", "natural gas" }, "Pay Gas Bill"),
            (new[] { "water bill", "sewer", "water usage", "water account" }, "Pay Water Bill"),
            (new[] { "car registration", "vehicle registration", "rego", "registration renewal", "renew your registration", "cazr rego" }, "Pay Car Registration"),
            (new[] { "council rates", "rates notice", "land rates" }, "Pay Council Rates"),
            (new[] { "rent" }, "Pay Rent"),
            (new[] { "body corporate", "strata levy", "strata levies", "owners corporation" }, "Pay Strata Levy"),
            (new[] { "insurance", "premium is due", "policy renewal" }, "Pay Insurance"),
            (new[] { "credit card", "card statement", "minimum payment" }, "Pay Credit Card"),
            (new[] { "ato", "tax return", "tax is due", "activity statement", "bas statement" }, "Pay Tax / ATO"),
            (new[] { "phone bill", "mobile bill", "telstra", "optus", "vodafone" }, "Pay Phone Bill"),
            (new[] { "internet", "nbn", "broadband" }, "Pay Internet Bill"),
            (new[] { "toll", "linkt", "e-tag", "etag" }, "Pay Tolls"),
            (new[] { "subscription", "renew your", "auto-renew", "membership renewal" }, "Renew Subscription"),
            (new[] { "appointment", "booking confirmation" }, "Appointment"),
            (new[] { "rsvp", "please respond", "confirm your attendance" }, "RSVP"),
            (new[] { "invoice", "amount due", "balance due", "statement", "payment due" }, "Pay Invoice"),
        };

        foreach (var (keys, title) in rules)
        {
            if (keys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
            {
                var vendor = VendorName(fromAddress);
                return title == "Pay Invoice" && vendor.Length > 0 ? $"Pay {vendor}" : title;
            }
        }

        var subj = (subject ?? string.Empty).Trim();
        return subj.Length > 0 ? $"Follow up: {Truncate(subj, 60)}" : "Follow up";
    }

    private static string VendorName(string? fromAddress)
    {
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            return string.Empty;
        }

        var at = fromAddress.LastIndexOf('@');
        if (at < 0 || at == fromAddress.Length - 1)
        {
            return string.Empty;
        }

        var host = fromAddress[(at + 1)..].Split('.');
        var label = host.Length >= 2 ? host[^2] : host[0];

        if (label is "mail" or "email" or "send" or "notifications" or "no-reply" or "noreply" && host.Length >= 3)
        {
            label = host[^3];
        }

        return label.Length == 0 ? string.Empty : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
