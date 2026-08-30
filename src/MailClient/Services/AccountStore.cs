using MailClient.Models;

namespace MailClient.Services;

/// Persists configured accounts to accounts.json (passwords DPAPI-encrypted).
public static class AccountStore
{
    private static readonly JsonFileStore<List<MailAccount>> Store = new("accounts.json", () => new());

    public static event EventHandler? Changed;

    public static IReadOnlyList<MailAccount> All => Store.Load();

    public static MailAccount? Find(string id) => Store.Load().FirstOrDefault(a => a.Id == id);

    public static void AddOrUpdate(MailAccount account)
    {
        var list = Store.Load();
        list.RemoveAll(a => a.Id == account.Id);
        list.Add(account);
        Store.Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(string id)
    {
        var list = Store.Load();
        list.RemoveAll(a => a.Id == id);
        Store.Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string PasswordOf(MailAccount account) => SecretProtector.Unprotect(account.ProtectedPassword);
}
