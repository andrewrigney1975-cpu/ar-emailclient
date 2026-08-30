using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml.Controls;

namespace MailClient.Views;

public sealed partial class AddAccountDialog : ContentDialog
{
    private readonly MailAccount? _existing;
    private bool _usernameEdited;

    public AddAccountDialog()
    {
        InitializeComponent();
        UsernameBox.TextChanged += (_, _) => _usernameEdited = true;
    }

    /// Opens the dialog pre-filled to edit an existing account. The password field is left blank;
    /// leaving it blank keeps the stored password.
    public AddAccountDialog(MailAccount existing)
        : this()
    {
        _existing = existing;
        _usernameEdited = true;

        Title = "Edit account";
        PrimaryButtonText = "Test & save";

        DisplayNameBox.Text = existing.DisplayName;
        EmailBox.Text = existing.Email;
        ImapHostBox.Text = existing.ImapHost;
        ImapPortBox.Value = existing.ImapPort;
        ImapSslBox.IsChecked = existing.ImapUseSsl;
        SmtpHostBox.Text = existing.SmtpHost;
        SmtpPortBox.Value = existing.SmtpPort;
        SmtpSslBox.IsChecked = existing.SmtpUseSsl;
        UsernameBox.Text = existing.Username;

        PasswordBox.Header = "Password (leave blank to keep current)";
    }

    private void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_usernameEdited)
        {
            UsernameBox.Text = EmailBox.Text;
        }

        GuessServers(EmailBox.Text);
    }

    /// A few well-known providers so the common case is one field + a password.
    private void GuessServers(string email)
    {
        var at = email.IndexOf('@');
        if (at < 0 || at == email.Length - 1)
        {
            return;
        }

        var domain = email[(at + 1)..].ToLowerInvariant();
        (string imap, string smtp)? known = domain switch
        {
            "gmail.com" or "googlemail.com" => ("imap.gmail.com", "smtp.gmail.com"),
            "outlook.com" or "hotmail.com" or "live.com" => ("outlook.office365.com", "smtp.office365.com"),
            "yahoo.com" => ("imap.mail.yahoo.com", "smtp.mail.yahoo.com"),
            "icloud.com" or "me.com" => ("imap.mail.me.com", "smtp.mail.me.com"),
            "fastmail.com" => ("imap.fastmail.com", "smtp.fastmail.com"),
            _ => null,
        };

        if (known is { } k)
        {
            if (string.IsNullOrWhiteSpace(ImapHostBox.Text)) ImapHostBox.Text = k.imap;
            if (string.IsNullOrWhiteSpace(SmtpHostBox.Text)) SmtpHostBox.Text = k.smtp;
        }
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true; // keep open until verified

        var password = PasswordBox.Password;
        var keepExistingPassword = _existing is not null && string.IsNullOrEmpty(password);

        if (string.IsNullOrWhiteSpace(EmailBox.Text) || (string.IsNullOrEmpty(password) && !keepExistingPassword) ||
            string.IsNullOrWhiteSpace(ImapHostBox.Text) || string.IsNullOrWhiteSpace(SmtpHostBox.Text))
        {
            Show(InfoBarSeverity.Warning, "Fill in email, password, and both server names.");
            deferral.Complete();
            return;
        }

        if (keepExistingPassword)
        {
            password = SecretProtector.Unprotect(_existing!.ProtectedPassword);
        }

        var account = new MailAccount
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            DisplayName = DisplayNameBox.Text.Trim(),
            Email = EmailBox.Text.Trim(),
            Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? EmailBox.Text.Trim() : UsernameBox.Text.Trim(),
            ImapHost = ImapHostBox.Text.Trim(),
            ImapPort = (int)ImapPortBox.Value,
            ImapUseSsl = ImapSslBox.IsChecked == true,
            SmtpHost = SmtpHostBox.Text.Trim(),
            SmtpPort = (int)SmtpPortBox.Value,
            SmtpUseSsl = SmtpSslBox.IsChecked == true,
        };

        Show(InfoBarSeverity.Informational, "Testing connection...");
        IsPrimaryButtonEnabled = false;

        try
        {
            await MailService.VerifyAsync(account, password, CancellationToken.None);
            account.ProtectedPassword = SecretProtector.Protect(password);

            if (_existing is not null)
            {
                // Drop the cached connection so the new settings take effect immediately.
                MailService.Disconnect(account.Id);
            }

            AccountStore.AddOrUpdate(account);
            args.Cancel = false;
            Hide();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AddAccountDialog.OnPrimary", ex);
            Show(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
