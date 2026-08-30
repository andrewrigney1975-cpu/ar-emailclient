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
        }
        catch (Exception ex)
        {
            LogCrash(ex, "OnLaunched");
            throw;
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
