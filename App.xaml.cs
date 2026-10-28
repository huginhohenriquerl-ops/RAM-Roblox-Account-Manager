using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RAM.Core;

namespace RAM;

public partial class App : Application
{
    // RAM_INSTANCE (development only) lets a dev build run next to the installed copy.
    static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("RAM_INSTANCE") ?? "";
    static readonly string SingletonName = @"Local\RAM.Clawd.Singleton" + InstanceSuffix;
    static readonly string ShowSignalName = @"Local\RAM.Clawd.Show" + InstanceSuffix;

    Mutex? _singleton;
    EventWaitHandle? _showSignal;

    public static AppSettings Settings { get; private set; } = null!;
    public static AccountStore Store { get; private set; } = null!;
    public static RobloxApi Api { get; } = new();
    public static MultiInstanceManager Multi { get; } = new();
    public static SessionIsolation Session { get; } = new();
    public static InstanceTracker Tracker { get; private set; } = null!;
    public static AppUpdater Updater { get; } = new();

    public static bool IsDark { get; private set; }
    public static event EventHandler? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleton = new Mutex(true, SingletonName, out bool firstInstance);
        if (!firstInstance)
        {
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true, Name = "show-signal" }.Start();

        Paths.Ensure();
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("fatal", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task", args.Exception);
            args.SetObserved();
        };
        Settings = AppSettings.Load();
        ApplyTheme(Settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Settings.Theme == "system") Dispatcher.BeginInvoke(() => ApplyTheme("system"));
        };

        Store = AccountStore.Load();
        Tracker = new InstanceTracker(Store.Accounts) { RenameWindows = () => Settings.RenameWindows };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // With OnExplicitShutdown a failed window would leave an invisible zombie process.
            Log.Error("startup", ex);
            MessageBox.Show("O R.A.M não conseguiu iniciar:\n\n" + ex.Message, "R.A.M", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    public static void ApplyTheme(string theme)
    {
        bool dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;
        var dictionaries = Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("unhandled", e.Exception);
        (MainWindow as MainWindow)?.ShowToast("Algo deu errado: " + e.Exception.Message, ToastKind.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Multi.Dispose();
        Session.Dispose();
        Store?.Save();
        Settings?.Save();
        try { _singleton?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
