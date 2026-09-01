using MailClient.Services;
using Microsoft.UI.Xaml;

namespace MailClient;

public partial class App : Application
{
    private MainWindow? _window;

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
            Services.OfficePreview.RegisterLicense();
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

    /// Called (on a background thread) by Program when another launch is redirected here - e.g. a
    /// "new mail" toast clicked while the app is already running.
    public static void HandleActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        if (Current is not App { _window: { } window })
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.BringToForeground();
            if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification &&
                args.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs n &&
                Services.NotificationService.ParseMailRef(n.Arguments) is { } mail)
            {
                window.OpenFromNotification(mail);
            }
        });
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
                _window is { } mainWindow)
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
