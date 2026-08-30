using System.Security.Cryptography;
using System.Text;

namespace MailClient.Services;

/// DPAPI (current-user scope) wrapper for the one secret this MVP stores: the account password /
/// app-password. Ciphertext is base64 and only decryptable by this Windows user on this machine.
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinUI3Mail.v1");

    public static string Protect(string plaintext)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedBase64)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            LoggingService.Warn("SecretProtector.Unprotect", ex);
            return string.Empty;
        }
    }
}
