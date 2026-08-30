using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MailClient.Services;

/// A message a toast can deep-link to.
public sealed record MailRef(string AccountId, string Folder, uint Uid);

/// Thin wrapper around Windows toast notifications (modelled on the file-explorer project). This
/// app is unpackaged, so AppNotificationManager is the least battle-tested corner - every call is
/// defensive so a platform quirk never crashes or blocks the app.
public static class NotificationService
{
    private static bool _registered;

    /// Raised (on a background thread) when the user clicks a "new mail" toast.
    public static event Action<MailRef>? MailActivated;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("NotificationService.Register", ex);
            _registered = false;
        }
    }

    public static void Unregister()
    {
        try
        {
            if (_registered)
            {
                AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
                AppNotificationManager.Default.Unregister();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("NotificationService.Unregister", ex);
        }
    }

    public static void Show(string title, string message)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"NotificationService.Show: {title}", ex);
        }
    }

    /// A toast that, when clicked, deep-links to a specific message.
    public static void ShowNewMail(string title, string message, MailRef target)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "openMail")
                .AddArgument("account", target.AccountId)
                .AddArgument("folder", target.Folder)
                .AddArgument("uid", target.Uid.ToString())
                .AddText(title)
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"NotificationService.ShowNewMail: {title}", ex);
        }
    }

    /// Parses toast arguments (from an in-process invoke or a cold-launch activation) into a MailRef.
    public static MailRef? ParseMailRef(IDictionary<string, string> arguments)
    {
        if (arguments.TryGetValue("action", out var action) && action == "openMail" &&
            arguments.TryGetValue("account", out var account) &&
            arguments.TryGetValue("folder", out var folder) &&
            arguments.TryGetValue("uid", out var uidText) &&
            uint.TryParse(uidText, out var uid))
        {
            return new MailRef(account, folder, uid);
        }

        return null;
    }

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (ParseMailRef(args.Arguments) is { } mail)
            {
                MailActivated?.Invoke(mail);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("NotificationService.OnInvoked", ex);
        }
    }
}
