using MailClient.Models;

namespace MailClient.Services;

/// The user's address book (address-book.json): rich contacts plus a user-extensible set of
/// groups. Distinct from <see cref="ContactStore"/>, which is just addresses harvested from mail
/// for auto-complete — <see cref="SearchEmails"/> here feeds the same auto-complete from real
/// contacts.
public static class AddressBook
{
    public sealed class Data
    {
        public List<Contact> Contacts { get; set; } = new();
        public List<ContactGroup> Groups { get; set; } = new();
    }

    private static readonly JsonFileStore<Data> Store = new("address-book.json", () => new Data());

    /// Raised after any change so open views can refresh.
    public static event EventHandler? Changed;

    private static void Save(Data data)
    {
        Store.Save(data);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<Contact> Contacts => Store.Load().Contacts;

    public static IReadOnlyList<ContactGroup> Groups => Store.Load().Groups;

    public static Contact? Find(string id) => Store.Load().Contacts.FirstOrDefault(c => c.Id == id);

    public static void Upsert(Contact contact)
    {
        var data = Store.Load();
        var i = data.Contacts.FindIndex(c => c.Id == contact.Id);
        if (i >= 0)
        {
            data.Contacts[i] = contact;
        }
        else
        {
            data.Contacts.Add(contact);
        }

        // Any group named on the contact that we don't know yet becomes a real group.
        foreach (var g in contact.Groups)
        {
            if (!string.IsNullOrWhiteSpace(g) && data.Groups.All(x => !x.Name.Equals(g, StringComparison.OrdinalIgnoreCase)))
            {
                data.Groups.Add(new ContactGroup { Name = g });
            }
        }

        Save(data);
    }

    public static void Delete(string id)
    {
        var data = Store.Load();
        data.Contacts.RemoveAll(c => c.Id == id);
        Save(data);
    }

    public static void SetFavourite(string id, bool favourite)
    {
        var data = Store.Load();
        if (data.Contacts.FirstOrDefault(c => c.Id == id) is { } c)
        {
            c.IsFavourite = favourite;
            Save(data);
        }
    }

    // ----- groups -----

    public static void AddGroup(string name)
    {
        name = name.Trim();
        var data = Store.Load();
        if (name.Length > 0 && data.Groups.All(g => !g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            data.Groups.Add(new ContactGroup { Name = name });
            Save(data);
        }
    }

    public static void RenameGroup(string oldName, string newName)
    {
        newName = newName.Trim();
        var data = Store.Load();
        if (newName.Length == 0 || data.Groups.FirstOrDefault(g => g.Name == oldName) is not { } group)
        {
            return;
        }

        group.Name = newName;
        foreach (var c in data.Contacts)
        {
            for (var i = 0; i < c.Groups.Count; i++)
            {
                if (c.Groups[i].Equals(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    c.Groups[i] = newName;
                }
            }
        }

        Save(data);
    }

    public static void DeleteGroup(string name)
    {
        var data = Store.Load();
        data.Groups.RemoveAll(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var c in data.Contacts)
        {
            for (var i = c.Groups.Count - 1; i >= 0; i--)
            {
                if (c.Groups[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    c.Groups.RemoveAt(i);
                }
            }
        }

        Save(data);
    }

    public static void SetGroupFavourite(string name, bool favourite)
    {
        var data = Store.Load();
        if (data.Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } g)
        {
            g.IsFavourite = favourite;
            Save(data);
        }
    }

    // ----- auto-complete -----

    /// "Name <email>" strings for contacts whose name or any email matches `query`.
    public static IEnumerable<string> SearchEmails(string query, int limit = 8)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            yield break;
        }

        var count = 0;
        foreach (var c in Store.Load().Contacts.OrderByDescending(c => c.IsFavourite).ThenBy(c => c.SortKey))
        {
            var nameMatch = c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase);

            foreach (var e in c.Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)))
            {
                if (nameMatch || e.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    yield return c.DisplayName.Length > 0 && c.DisplayName != "(no name)"
                        ? $"{c.DisplayName} <{e.Value}>"
                        : e.Value;
                    if (++count >= limit)
                    {
                        yield break;
                    }
                }
            }
        }
    }
}
