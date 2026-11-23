using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FanControl.Models;
using FanControl.Services;
using FanControl.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FanControl;

public partial class App : Application
{
    private const string MutexName = @"Local\Fan-control.Quee88.SingleInstance";
    private const string ShowEventName = @"Local\Fan-control.Quee88.Show";
    private const string ExitEventName = @"Local\Fan-control.Quee88.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private HardwareService? _hardware;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private bool _watchingSystemTheme;

    public static bool RestartRequested { get; set; }
    public static bool IsExiting { get; private set; }
    public SettingsService Settings { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // „FanControl.exe --exit“ korektně ukončí běžící instanci (větráky se vrátí systému).
        if (e.Args.Contains("--exit"))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { /* neběží */ }
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance(e.Args.Contains("--restart")))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Crash(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Nezachycená výjimka úlohy.", ex.Exception); ex.SetObserved(); };
        SessionEnding += (_, _) => ExitApp();

        Settings.Load();
        Log.Info($"Start (správce: {SystemInfo.IsAdministrator()}, argumenty: {string.Join(' ', e.Args)}).");

        _hardware = new HardwareService();
        _vm = new MainViewModel(_hardware, Settings);
        _window = new MainWindow(_vm);
        MainWindow = _window;

        ApplyTheme(Settings.Current.Theme);
        _vm.ThemeChanged += ApplyTheme;

        _tray = new TrayIcon(ShowMainWindow, () => _vm.ResetAllToAutoCommand.Execute(null), ExitApp);
        _vm.TrayTextChanged += text => _tray?.SetText(text);

        // Pokud se aplikace přesunula, opravíme cestu v naplánované úloze.
        if (Settings.Current.StartWithWindows && SystemInfo.IsAdministrator())
        {
            Task.Run(() => StartupService.Enable(out _));
        }

        var silent = e.Args.Contains("--autostart") && Settings.Current.StartMinimized;
        if (!silent)
        {
            ShowMainWindow();
        }

        _hardware.Start();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _mutex = new Mutex(false, MutexName);
        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            // Už běží – jen ji přeneseme do popředí.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* nic */ }
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                if (IsExiting) return;
                if (which == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                else Dispatcher.BeginInvoke(ExitApp);
            }
        }) { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
        return true;
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>Okno se zavřelo křížkem – podle nastavení skrýt do lišty, nebo ukončit.</summary>
    public void OnWindowClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsExiting) return;
        if (!Settings.Current.CloseToTray)
        {
            ExitApp();
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        if (!Settings.Current.TrayHintShown)
        {
            Settings.Current.TrayHintShown = true;
            Settings.SaveSoon();
            _tray?.ShowHint("Fan Control běží dál",
                "Větráky se řídí i se zavřeným oknem. Aplikaci najdeš v oznamovací oblasti vedle hodin.");
        }
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        if (_hardware is null)
        {
            // Pomocná instance (--exit nebo druhé spuštění) – nic neuklízí a hlavně nepřepisuje nastavení.
            base.OnExit(e);
            return;
        }

        try { _hardware?.Dispose(); } catch (Exception ex) { Log.Error("Chyba při ukončení.", ex); }
        _tray?.Dispose();
        Settings.SaveNow();
        Log.Info("Konec.");
        try { _mutex?.ReleaseMutex(); } catch { /* nic */ }
        _mutex?.Dispose();
        _showEvent?.Set();
        base.OnExit(e);
    }

    private void ApplyTheme(AppTheme theme)
    {
        if (_window is null) return;
        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme(true);
            if (!_watchingSystemTheme)
            {
                SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, true);
                _watchingSystemTheme = true;
            }
        }
        else
        {
            if (_watchingSystemTheme)
            {
                SystemThemeWatcher.UnWatch(_window);
                _watchingSystemTheme = false;
            }
            ApplicationThemeManager.Apply(theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
                WindowBackdropType.Mica, true);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Neošetřená chyba v UI.", e.Exception);
        e.Handled = true;
    }

    private void Crash(Exception? ex)
    {
        // Při pádu vždy vrátit větráky systému, aby nezůstaly na nízkých otáčkách.
        Log.Error("Aplikace spadla.", ex);
        try { _hardware?.RestoreDefaults(); } catch { /* nic */ }
    }
}
