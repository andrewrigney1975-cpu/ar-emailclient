using MailClient.Services;
using Microsoft.UI.Xaml;

namespace MailClient;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception, "AppDomain");
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception, "XamlUnhandled");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services.NotificationService.Register();
            _window = new MainWindow();
            _window.Activate();

            RouteNotificationLaunch();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "OnLaunched");
            throw;
        }
    }

    /// If the app was started (cold) by clicking a "new mail" toast, open that message.
    private void RouteNotificationLaunch()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification &&
                activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs n &&
                Services.NotificationService.ParseMailRef(n.Arguments) is { } mail &&
                _window is MainWindow mainWindow)
            {
                mainWindow.OpenFromNotification(mail);
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex, "RouteNotificationLaunch");
        }
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
        }
        catch
        {
            // best effort
        }
    }
}
