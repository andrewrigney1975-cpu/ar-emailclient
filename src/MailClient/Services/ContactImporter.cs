using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailClient.Models;

namespace MailClient.Services;

/// Best-effort contact import from CSV / JSON / HTML. The goal is low friction: pull out whatever
/// names, emails and phone numbers we can and let the user tidy up afterwards.
public static class ContactImporter
{
    public static List<Contact> Parse(string path)
    {
        var text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json" => FromJson(text),
            ".htm" or ".html" => FromHtml(text),
            _ => FromCsv(text),
        };
    }

    /// Adds parsed contacts to the address book, skipping ones whose primary email already exists.
    public static int Merge(IEnumerable<Contact> contacts)
    {
        var existing = AddressBook.Contacts
            .SelectMany(c => c.Emails.Select(e => e.Value))
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var c in contacts)
        {
            var email = c.PrimaryEmail;
            if (email.Length > 0 && !existing.Add(email))
            {
                continue;
            }

            if (c.DisplayName == "(no name)" && email.Length == 0 && c.PrimaryPhone.Length == 0)
            {
                continue;
            }

            AddressBook.Upsert(c);
            added++;
        }

        return added;
    }

    // ----- CSV -----

    private static List<Contact> FromCsv(string text)
    {
        var rows = ParseCsvRows(text);
        var result = new List<Contact>();
        if (rows.Count < 2)
        {
            return result;
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Any(n => h == n || h.Contains(n)));

        var iFirst = Col("first name", "given name", "first");
        var iLast = Col("last name", "family name", "surname", "last");
        var iName = Col("name", "display name", "full name");
        var iNick = Col("nickname", "nick");
        var iCompany = Col("company", "organization", "organisation");
        var iEmail = Col("e-mail address", "email address", "email", "e-mail", "mail");
        var iEmail2 = header.FindLastIndex(h => h.Contains("email") || h.Contains("e-mail"));
        var iPhoneMobile = Col("mobile", "cell");
        var iPhoneHome = Col("home phone", "home");
        var iPhoneWork = Col("business phone", "work phone", "work");
        var iPhone = Col("phone", "telephone");
        var iBirthday = Col("birthday", "date of birth", "dob", "born");
        var iNotes = Col("notes", "note");

        foreach (var row in rows.Skip(1))
        {
            string Get(int i) => i >= 0 && i < row.Count ? row[i].Trim() : string.Empty;

            var c = new Contact
            {
                FirstName = Get(iFirst),
                LastName = Get(iLast),
                Nickname = Get(iNick),
                Company = Get(iCompany),
                Notes = Get(iNotes),
            };

            if (c.FirstName.Length == 0 && c.LastName.Length == 0 && Get(iName) is { Length: > 0 } full)
            {
                var parts = full.Split(' ', 2);
                c.FirstName = parts[0];
                c.LastName = parts.Length > 1 ? parts[1] : string.Empty;
            }

            AddEmail(c, Get(iEmail), "personal");
            if (iEmail2 != iEmail)
            {
                AddEmail(c, Get(iEmail2), "work");
            }

            AddPhone(c, Get(iPhoneMobile), "personal mobile");
            AddPhone(c, Get(iPhoneHome), "personal landline");
            AddPhone(c, Get(iPhoneWork), "work landline");
            if (c.Phones.Count == 0)
            {
                AddPhone(c, Get(iPhone), "personal mobile");
            }

            if (Get(iBirthday) is { Length: > 0 } bd && DateTimeOffset.TryParse(bd, out var dob))
            {
                c.DateOfBirth = dob;
            }

            if (c.PrimaryEmail.Length > 0 || c.PrimaryPhone.Length > 0 || c.DisplayName != "(no name)")
            {
                result.Add(c);
            }
        }

        return result;
    }

    private static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') { inQuotes = false; }
                else { field.Append(ch); }
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default: field.Append(ch); break;
                }
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows.Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
    }

    // ----- JSON -----

    private static List<Contact> FromJson(string text)
    {
        var result = new List<Contact>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { return result; }

        using (doc)
        {
            // Our own export shape: { "Contacts": [ ... ] }
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Contacts", out var contactsEl))
            {
                root = contactsEl;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string Str(params string[] names)
                {
                    foreach (var n in names)
                    {
                        foreach (var p in el.EnumerateObject())
                        {
                            if (p.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                            {
                                return p.Value.GetString() ?? string.Empty;
                            }
                        }
                    }

                    return string.Empty;
                }

                var c = new Contact
                {
                    FirstName = Str("firstName", "first", "givenName"),
                    LastName = Str("lastName", "last", "familyName", "surname"),
                    Nickname = Str("nickname", "nick"),
                    Company = Str("company", "organization", "organisation"),
                    Notes = Str("notes", "note"),
                };

                var name = Str("name", "displayName", "fullName");
                if (c.FirstName.Length == 0 && c.LastName.Length == 0 && name.Length > 0)
                {
                    var parts = name.Split(' ', 2);
                    c.FirstName = parts[0];
                    c.LastName = parts.Length > 1 ? parts[1] : string.Empty;
                }

                AddEmail(c, Str("email", "emailAddress", "mail"), "personal");
                AddEmail(c, Str("workEmail", "email2"), "work");
                AddPhone(c, Str("mobile", "cell", "phone", "telephone"), "personal mobile");
                AddPhone(c, Str("workPhone", "businessPhone"), "work landline");

                // Our export shape carries typed arrays.
                foreach (var (prop, target, kind) in new[]
                         {
                             ("Emails", c.Emails, "personal"), ("Phones", c.Phones, "personal mobile"),
                             ("Addresses", c.Addresses, "home"),
                         })
                {
                    if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in arr.EnumerateArray())
                        {
                            var v = f.TryGetProperty("Value", out var vv) ? vv.GetString() : null;
                            var t = f.TryGetProperty("Type", out var tt) ? tt.GetString() : kind;
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                target.Add(new ContactField { Type = t ?? kind, Value = v!.Trim() });
                            }
                        }
                    }
                }

                if (Str("dateOfBirth", "birthday", "dob") is { Length: > 0 } bd && DateTimeOffset.TryParse(bd, out var dob))
                {
                    c.DateOfBirth = dob;
                }

                if (c.PrimaryEmail.Length > 0 || c.PrimaryPhone.Length > 0 || c.DisplayName != "(no name)")
                {
                    result.Add(c);
                }
            }
        }

        return result;
    }

    // ----- HTML -----

    private static List<Contact> FromHtml(string text)
    {
        var result = new List<Contact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) mailto: links, using the link text as the name when it isn't itself an address.
        foreach (Match m in Regex.Matches(text,
                     @"<a[^>]*href\s*=\s*[""']mailto:([^""'?]+)[""'][^>]*>(.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var email = WebUtility(m.Groups[1].Value.Trim());
            var label = Regex.Replace(m.Groups[2].Value, "<[^>]+>", string.Empty).Trim();
            AddFromNameEmail(result, seen, label, email);
        }

        // 2) bare addresses anywhere in the text.
        foreach (Match m in Regex.Matches(text, @"[\w.+-]+@[\w-]+\.[\w.-]+"))
        {
            AddFromNameEmail(result, seen, string.Empty, m.Value.Trim());
        }

        return result;
    }

    private static string WebUtility(string s) => System.Net.WebUtility.HtmlDecode(s);

    private static void AddFromNameEmail(List<Contact> result, HashSet<string> seen, string name, string email)
    {
        if (email.Length == 0 || !seen.Add(email))
        {
            return;
        }

        var c = new Contact();
        if (name.Length > 0 && !name.Contains('@'))
        {
            var parts = name.Split(' ', 2);
            c.FirstName = parts[0];
            c.LastName = parts.Length > 1 ? parts[1] : string.Empty;
        }

        c.Emails.Add(new ContactField { Type = "personal", Value = email });
        result.Add(c);
    }

    private static void AddEmail(Contact c, string value, string type)
    {
        value = value.Trim();
        if (value.Length > 0 && value.Contains('@') && c.Emails.All(e => !e.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            c.Emails.Add(new ContactField { Type = type, Value = value });
        }
    }

    private static void AddPhone(Contact c, string value, string type)
    {
        value = value.Trim();
        if (value.Length > 0 && c.Phones.All(p => p.Value != value))
        {
            c.Phones.Add(new ContactField { Type = type, Value = value });
        }
    }
}
