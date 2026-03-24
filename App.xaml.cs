using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace Via;

public partial class App : System.Windows.Application
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VIA x4", "logs", "via.log");

    private static Mutex? _singleInstanceMutex;

    internal const string AppAumid = "VIA.VIAx4";

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appId);

    private static void RegisterAppForNotifications()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppAumid);
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Classes\AppUserModelId\" + AppAumid);
            if (key == null) return;
            key.SetValue("DisplayName", "VIA x4");
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) key.SetValue("IconUri", exe);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterAppForNotifications();

        // Single-instance enforcement
        _singleInstanceMutex = new Mutex(true, "Global\\VIA_x4_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show(
                "VIA x4 is already running.",
                "VIA x4",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Follow the Windows system theme (light or dark)
        var sysTheme = ApplicationThemeManager.GetSystemTheme();
        ApplicationThemeManager.Apply(
            sysTheme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogException("AppDomain", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("TaskScheduler", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("Dispatcher", args.Exception);
            // Let truly fatal exceptions (OutOfMemory, StackOverflow) crash the app
            if (args.Exception is OutOfMemoryException or StackOverflowException)
                return; // args.Handled stays false → app terminates
            args.Handled = true;
            // Show a non-intrusive error so the user knows something went wrong
            try
            {
                System.Windows.MessageBox.Show(
                    $"An unexpected error occurred:\n{args.Exception.Message}\n\nThe app will try to continue. Check logs for details.",
                    "VIA x4 — Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch { }
        };
    }

    private static void LogException(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED [{source}] {ex?.GetType().Name}: {ex?.Message}\n";
            File.AppendAllText(LogFile, line);
        }
        catch { }
    }
}
