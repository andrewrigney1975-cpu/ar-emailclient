using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MimeKit;

namespace MailClient.Views;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward,
}

public sealed partial class ComposeWindow : Window
{
    private readonly MailAccount _account;
    private readonly MailMessageContent? _source;

    public ComposeWindow(MailAccount account, ComposeMode mode, MailMessageContent? source)
    {
        InitializeComponent();
        _account = account;
        _source = source;

        Title = "New message";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Prefill(mode, source);
    }

    private void Prefill(ComposeMode mode, MailMessageContent? source)
    {
        if (source is null || mode == ComposeMode.New)
        {
            return;
        }

        var quotedHeader = $"On {source.Date.LocalDateTime:f}, {source.FromDisplay} wrote:";
        var quoted = string.Join("\n", (source.PlainText ?? StripHtml(source.Html) ?? string.Empty)
            .Split('\n').Select(l => "> " + l));
        var body = $"\n\n{quotedHeader}\n{quoted}\n";

        switch (mode)
        {
            case ComposeMode.Reply:
                ToBox.Text = source.ReplyToAddress;
                SubjectBox.Text = Prefixed("Re:", source.Subject);
                BodyBox.Text = body;
                break;
            case ComposeMode.ReplyAll:
                ToBox.Text = source.ReplyToAddress;
                CcBox.Text = source.CcDisplay;
                SubjectBox.Text = Prefixed("Re:", source.Subject);
                BodyBox.Text = body;
                break;
            case ComposeMode.Forward:
                SubjectBox.Text = Prefixed("Fwd:", source.Subject);
                BodyBox.Text = $"\n\n---------- Forwarded message ----------\nFrom: {source.FromDisplay}\n" +
                               $"Date: {source.Date.LocalDateTime:f}\nSubject: {source.Subject}\nTo: {source.ToDisplay}\n\n" +
                               (source.PlainText ?? StripHtml(source.Html) ?? string.Empty);
                break;
        }

        Title = SubjectBox.Text;
    }

    private static string Prefixed(string prefix, string subject) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? subject : $"{prefix} {subject}";

    private static string? StripHtml(string? html) =>
        html is null ? null : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty);

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var recipients = ParseAddresses(ToBox.Text).ToList();
        if (recipients.Count == 0)
        {
            Status(InfoBarSeverity.Warning, "Add at least one recipient.");
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(_account.DisplayName) ? _account.Email : _account.DisplayName, _account.Email));
        message.To.AddRange(recipients);
        message.Cc.AddRange(ParseAddresses(CcBox.Text));
        message.Subject = SubjectBox.Text;
        message.Body = new TextPart("plain") { Text = BodyBox.Text };

        if (_source is { MessageId.Length: > 0 } src)
        {
            message.InReplyTo = src.MessageId;
            foreach (var reference in src.References.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                message.References.Add(reference);
            }

            message.References.Add(src.MessageId);
        }

        SendButton.IsEnabled = false;
        Status(InfoBarSeverity.Informational, "Sending...");

        try
        {
            await Task.Run(() => MailService.SendAsync(_account, message, CancellationToken.None));
            Close();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ComposeWindow.Send", ex);
            Status(InfoBarSeverity.Error, "Send failed: " + ex.Message);
            SendButton.IsEnabled = true;
        }
    }

    private static IEnumerable<MailboxAddress> ParseAddresses(string raw)
    {
        foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MailboxAddress.TryParse(part.Trim(), out var address))
            {
                yield return address;
            }
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => Close();

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
