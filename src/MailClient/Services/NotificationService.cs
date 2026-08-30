using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MailClient.Services;

/// Thin wrapper around Windows toast notifications (modelled on the file-explorer project). This
/// app is unpackaged, so AppNotificationManager is the least battle-tested corner - every call is
/// defensive so a platform quirk never crashes or blocks the app.
public static class NotificationService
{
    private static bool _registered;

    public static void Register()
    {
        try
        {
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
}
