namespace MailClient.Models;

/// A configured IMAP+SMTP account. The password is stored DPAPI-encrypted (see SecretProtector);
/// it is never written in the clear.
public sealed class MailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    /// DPAPI-encrypted, base64. Decrypt with SecretProtector.Unprotect.
    public string ProtectedPassword { get; set; } = string.Empty;
}
