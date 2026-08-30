using System.Runtime.InteropServices;
using MailClient.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MailClient;

/// Custom entry point: registers this process as the single instance for the app key and redirects
/// any further activations (including "new mail" toast clicks) to the already-running window.
public static class Program
{
    private const uint CwmoDefault = 0;
    private const uint Infinite = 0xFFFFFFFF;

    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
        {
            return; // handed off to the primary instance
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool DecideRedirection()
    {
        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            var keyInstance = AppInstance.FindOrRegisterForKey("winui3-mailclient");

            if (keyInstance.IsCurrent)
            {
                keyInstance.Activated += (_, e) => App.HandleActivation(e);
                return false;
            }

            RedirectActivationTo(args, keyInstance);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Program.DecideRedirection", ex);
            return false; // fall back to a normal launch
        }
    }

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        _ = CoWaitForMultipleObjects(CwmoDefault, Infinite, 1, new[] { redirectEventHandle }, out _);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);
}
