namespace MailClient.Services;

/// Sender domains the user has chosen to always load remote images for (remote-image-domains.json).
public static class RemoteContentStore
{
    private static readonly JsonFileStore<List<string>> Store =
        new("remote-image-domains.json", () => new List<string>());

    public static bool IsAllowed(string emailAddress)
    {
        var domain = DomainOf(emailAddress);
        return domain.Length > 0 &&
               Store.Load().Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
    }

    public static void Allow(string emailAddress)
    {
        var domain = DomainOf(emailAddress);
        if (domain.Length == 0)
        {
            return;
        }

        var list = Store.Load();
        if (!list.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(domain);
            Store.Save(list);
        }
    }

    public static string DomainOf(string emailAddress)
    {
        var at = emailAddress.LastIndexOf('@');
        return at >= 0 && at < emailAddress.Length - 1
            ? emailAddress[(at + 1)..].Trim().ToLowerInvariant()
            : string.Empty;
    }
}
