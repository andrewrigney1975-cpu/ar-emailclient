namespace MailClient.Services;

/// A local address book (contacts.json) built from senders/recipients seen in mail, used for
/// recipient auto-complete when composing.
public static class ContactStore
{
    // address -> display name ("" if unknown)
    private static readonly JsonFileStore<Dictionary<string, string>> Store =
        new("contacts.json", () => new Dictionary<string, string>());

    public static void RefreshFromCache() => RecordMany(MessageCache.KnownAddresses());

    public static void Record(string? address, string? name)
    {
        if (address is null || !LooksLikeEmail(address))
        {
            return;
        }

        var list = Store.Load();
        var key = address.Trim();
        if (!list.TryGetValue(key, out var existing) ||
            (string.IsNullOrEmpty(existing) && !string.IsNullOrWhiteSpace(name)))
        {
            list[key] = (name ?? string.Empty).Trim();
            Store.Save(list);
        }
    }

    public static void RecordMany(IEnumerable<(string Address, string Name)> items)
    {
        var list = Store.Load();
        var changed = false;

        foreach (var (address, name) in items)
        {
            if (!LooksLikeEmail(address))
            {
                continue;
            }

            var key = address.Trim();
            if (!list.TryGetValue(key, out var existing) ||
                (string.IsNullOrEmpty(existing) && !string.IsNullOrWhiteSpace(name)))
            {
                list[key] = (name ?? string.Empty).Trim();
                changed = true;
            }
        }

        if (changed)
        {
            Store.Save(list);
        }
    }

    /// Up to `limit` contacts whose address or name contains `query`, address-ordered.
    public static List<string> Search(string query, int limit = 8)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return new List<string>();
        }

        return Store.Load()
            .Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => string.IsNullOrEmpty(kv.Value) ? kv.Key : $"{kv.Value} <{kv.Key}>")
            .ToList();
    }

    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && !s.Contains(' ') && s.Contains('.', StringComparison.Ordinal);
    }
}
