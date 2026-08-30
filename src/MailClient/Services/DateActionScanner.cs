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

    private static readonly string[] DueCues =
    {
        "due", "payable", "pay by", "pay before", "payment due", "amount due", "balance due",
        "by the", "on or before", "no later than", "expires", "expiry", "renew", "renewal",
        "deadline", "rsvp", "respond by", "reply by", "closes", "closing date", "final date",
    };

    private const string MonthNames =
        "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|" +
        "sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    private static readonly Regex IsoDate = new(@"\b(\d{4})-(\d{2})-(\d{2})\b", RegexOptions.Compiled);

    private static readonly Regex NumericDate = new(
        @"\b(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})\b", RegexOptions.Compiled);

    private static readonly Regex DayMonthDate = new(
        $@"\b(\d{{1,2}})(?:st|nd|rd|th)?\s+({MonthNames})\b(?:\s+(\d{{4}}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthDayDate = new(
        $@"\b({MonthNames})\s+(\d{{1,2}})(?:st|nd|rd|th)?\b(?:,?\s+(\d{{4}}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CalendarSuggestion? Scan(string? subject, string? body, string? fromAddress)
    {
        var text = ((subject ?? string.Empty) + "\n" + (body ?? string.Empty)).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        var hasCue = DueCues.Any(c => lower.Contains(c));
        if (!hasCue)
        {
            return null;
        }

        var date = FindDate(text);
        if (date is null)
        {
            return null;
        }

        return new CalendarSuggestion(date.Value, InferTitle(lower, subject, fromAddress));
    }

    private static DateTimeOffset? FindDate(string text)
    {
        foreach (Match m in IsoDate.Matches(text))
        {
            if (TryMake(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), out var d))
            {
                return d;
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
                return d;
            }
        }

        foreach (Match m in DayMonthDate.Matches(text))
        {
            if (TryMonth(m.Groups[2].Value, out var month) &&
                TryMake(int.Parse(m.Groups[1].Value), month, YearOrGuess(m.Groups[3].Value, month, int.Parse(m.Groups[1].Value)), out var d))
            {
                return d;
            }
        }

        foreach (Match m in MonthDayDate.Matches(text))
        {
            if (TryMonth(m.Groups[1].Value, out var month) &&
                TryMake(int.Parse(m.Groups[2].Value), month, YearOrGuess(m.Groups[3].Value, month, int.Parse(m.Groups[2].Value)), out var d))
            {
                return d;
            }
        }

        return null;
    }

    private static int YearOrGuess(string captured, int month, int day)
    {
        if (int.TryParse(captured, out var y) && y > 1900)
        {
            return y;
        }

        var today = DateTime.Today;
        var candidate = new DateTime(today.Year, Math.Clamp(month, 1, 12), 1);

        // A date already well in the past this year almost always means next year's occurrence.
        return candidate.AddDays(Math.Min(day, 28) - 1) < today.AddDays(-14) ? today.Year + 1 : today.Year;
    }

    private static bool TryMake(int day, int month, int year, out DateTimeOffset value)
    {
        value = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || year is < 2000 or > 2100)
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
            if (t == name || (t.Length >= 3 && name.StartsWith(t)))
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
            (new[] { "electricity", "power bill", "energy", "kwh", "agl", "origin energy" }, "Pay Electricity Bill"),
            (new[] { "gas bill", "natural gas" }, "Pay Gas Bill"),
            (new[] { "water bill", "sewer", "water usage" }, "Pay Water Bill"),
            (new[] { "car registration", "vehicle registration", "rego", "registration renewal", "renew your registration" }, "Pay Car Registration"),
            (new[] { "council rates", "rates notice", "land rates" }, "Pay Council Rates"),
            (new[] { "rent" }, "Pay Rent"),
            (new[] { "body corporate", "strata levy", "strata levies" }, "Pay Strata Levy"),
            (new[] { "insurance", "premium is due", "policy renewal" }, "Pay Insurance"),
            (new[] { "credit card", "card statement", "minimum payment" }, "Pay Credit Card"),
            (new[] { "ato", "tax return", "tax is due", "bas ", "activity statement" }, "Pay Tax / ATO"),
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
            if (keys.Any(lower.Contains))
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

        // Drop common mail sub-labels.
        if (label is "mail" or "email" or "send" or "notifications" or "no-reply" or "noreply" && host.Length >= 3)
        {
            label = host[^3];
        }

        return label.Length == 0 ? string.Empty : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
