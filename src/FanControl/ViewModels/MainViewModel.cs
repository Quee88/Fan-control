using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FanControl.Models;
using FanControl.Services;
using LibreHardwareMonitor.Hardware;

namespace FanControl.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HardwareService _hardware;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, SensorViewModel> _sensorById = new();
    private bool _loadingSettings;

    public MainViewModel(HardwareService hardware, SettingsService settings)
    {
        _hardware = hardware;
        _settings = settings;

        IsAdministrator = SystemInfo.IsAdministrator();
        IsPawnIoInstalled = SystemInfo.IsPawnIoInstalled();

        _loadingSettings = true;
        ThemeIndex = (int)settings.Current.Theme;
        StartWithWindows = settings.Current.StartWithWindows;
        StartMinimized = settings.Current.StartMinimized;
        CloseToTray = settings.Current.CloseToTray;
        UpdateIntervalSeconds = settings.Current.UpdateIntervalMs / 1000.0;
        _loadingSettings = false;

        _hardware.IntervalMs = settings.Current.UpdateIntervalMs;
        _hardware.Discovered += d => Dispatch(() => OnDiscovered(d));
        _hardware.Updated += s => Dispatch(() => OnUpdated(s));
        _hardware.Failed += msg => Dispatch(() =>
        {
            IsLoading = false;
            LoadError = msg;
        });
    }

    public event Action<string>? TrayTextChanged;
    public event Action<AppTheme>? ThemeChanged;

    public ObservableCollection<FanViewModel> Fans { get; } = [];
    public ObservableCollection<SensorGroupViewModel> SensorGroups { get; } = [];
    public ObservableCollection<SensorOption> TemperatureOptions { get; } = [];
    public ObservableCollection<HeadlineViewModel> Headlines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFans))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);
    public bool ShowNoFans => !IsLoading && Fans.Count == 0;

    [ObservableProperty] private string _hardwareSummary = "";

    // ---------- Upozornění ----------

    public bool IsAdministrator { get; }
    public bool ShowAdminWarning => !IsAdministrator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPawnIoWarning), nameof(PawnIoStatus))]
    private bool _isPawnIoInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIoStatus))]
    private bool _isInstallingPawnIo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPawnIoWarning))]
    private bool _pawnIoNeedsRestart;

    public bool ShowPawnIoWarning => !IsPawnIoInstalled || PawnIoNeedsRestart;

    public string PawnIoStatus => IsInstallingPawnIo
        ? "Instaluji…"
        : PawnIoNeedsRestart ? "Nainstalováno – restartuj aplikaci"
        : IsPawnIoInstalled ? "Nainstalováno" : "Chybí";

    [RelayCommand]
    private async Task InstallPawnIo()
    {
        IsInstallingPawnIo = true;
        var ok = await SystemInfo.InstallPawnIoAsync();
        IsInstallingPawnIo = false;
        if (ok)
        {
            IsPawnIoInstalled = true;
            PawnIoNeedsRestart = true;
        }
        else
        {
            SystemInfo.OpenUrl("https://pawnio.eu/");
        }
    }

    [RelayCommand]
    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        App.RestartRequested = true;
        Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        ((App)Application.Current).ExitApp();
    }

    // ---------- Nastavení ----------

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateIntervalText))]
    private double _updateIntervalSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupError))]
    private string? _startupError;

    public bool HasStartupError => !string.IsNullOrEmpty(StartupError);
    public string UpdateIntervalText => $"{UpdateIntervalSeconds:0.##} s".Replace('.', ',');

    partial void OnThemeIndexChanged(int value)
    {
        if (_loadingSettings) return;
        _settings.Current.Theme = (AppTheme)value;
        _settings.SaveSoon();
        ThemeChanged?.Invoke((AppTheme)value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingSettings) return;
        StartupError = null;
        string? error;
        var ok = value ? StartupService.Enable(out error) : StartupService.Disable(out error);
        if (!ok)
        {
            StartupError = "Nastavení automatického spuštění se nepodařilo: " + error;
            _loadingSettings = true;
            StartWithWindows = !value;
            _loadingSettings = false;
            return;
        }
        _settings.Current.StartWithWindows = value;
        _settings.SaveSoon();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_loadingSettings) return;
        _settings.Current.StartMinimized = value;
        _settings.SaveSoon();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loadingSettings) return;
        _settings.Current.CloseToTray = value;
        _settings.SaveSoon();
    }

    partial void OnUpdateIntervalSecondsChanged(double value)
    {
        if (_loadingSettings) return;
        var ms = (int)Math.Round(value * 1000);
        _settings.Current.UpdateIntervalMs = ms;
        _hardware.IntervalMs = ms;
        _settings.SaveSoon();
    }

    [RelayCommand]
    private void ResetAllToAuto()
    {
        foreach (var fan in Fans) fan.Mode = FanMode.Auto;
    }

    [RelayCommand]
    private static void OpenDataFolder()
    {
        System.IO.Directory.CreateDirectory(SettingsService.Folder);
        SystemInfo.OpenUrl(SettingsService.Folder);
    }

    [RelayCommand]
    private static void OpenProjectPage() => SystemInfo.OpenUrl("https://github.com/Quee88/Fan-control");

    public string VersionText => "Verze " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");

    // ---------- Data z hardwaru ----------

    private void OnDiscovered(DiscoveryResult d)
    {
        HardwareSummary = string.Join(" · ", d.Hardware
            .Where(h => h.Type is HardwareType.Motherboard or HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .Select(h => h.Name.Trim())
            .Distinct());

        foreach (var s in d.Sensors.Where(s => s.SensorType == SensorType.Temperature)
                     .OrderBy(s => Order(s.HardwareType)).ThenBy(s => s.HardwareName))
        {
            TemperatureOptions.Add(new SensorOption(s.Id, $"{s.Name} · {s.HardwareName}"));
        }

        foreach (var group in d.Sensors.GroupBy(s => (s.HardwareName, s.HardwareType)).OrderBy(g => Order(g.Key.HardwareType)))
        {
            var vm = new SensorGroupViewModel(group.Key.HardwareName, group.Key.HardwareType);
            foreach (var s in group.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
            {
                var svm = new SensorViewModel(s);
                vm.Sensors.Add(svm);
                _sensorById[s.Id] = svm;
            }
            SensorGroups.Add(vm);
        }

        AddHeadline(d, HardwareType.Cpu, "Procesor", ["Package", "Core Max", "Core Average", "Tctl"]);
        foreach (var gpuType in new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd })
        {
            AddHeadline(d, gpuType, "Grafická karta", ["GPU Core", "Core", "Hot Spot"]);
        }

        foreach (var ch in d.Channels.OrderBy(c => Order(c.HardwareType)))
        {
            if (!_settings.Current.Fans.TryGetValue(ch.Id, out var fs))
            {
                fs = new FanSettings();
                _settings.Current.Fans[ch.Id] = fs;
            }
            var fan = new FanViewModel(ch, fs, TemperatureOptions, OnFanChanged);
            Fans.Add(fan);
            _hardware.SetRule(ch.Id, fan.ToRule());
        }

        IsLoading = false;
        OnPropertyChanged(nameof(ShowNoFans));
        _settings.SaveSoon();
    }

    private void AddHeadline(DiscoveryResult d, HardwareType type, string title, string[] preferred)
    {
        var temps = d.Sensors.Where(s => s.HardwareType == type && s.SensorType == SensorType.Temperature).ToList();
        if (temps.Count == 0) return;
        var pick = preferred.Select(p => temps.FirstOrDefault(t => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                       .FirstOrDefault(t => t is not null) ?? temps[0];
        Headlines.Add(new HeadlineViewModel(title, pick.HardwareName, pick.Id));
    }

    private void OnFanChanged(FanViewModel fan)
    {
        _hardware.SetRule(fan.Id, fan.ToRule());
        _settings.SaveSoon();
    }

    private void OnUpdated(HardwareSnapshot snapshot)
    {
        foreach (var (id, reading) in snapshot.Sensors)
        {
            if (_sensorById.TryGetValue(id, out var vm)) vm.Apply(reading);
        }

        foreach (var h in Headlines)
        {
            h.Value = snapshot.Sensors.TryGetValue(h.SensorId, out var r) ? r.Value : null;
        }

        foreach (var fan in Fans)
        {
            if (snapshot.Channels.TryGetValue(fan.Id, out var state)) fan.Apply(state);
            fan.LiveSourceTemperature = fan.SelectedSource is { } src && snapshot.Sensors.TryGetValue(src.Id, out var t)
                ? t.Value
                : null;
        }

        TrayTextChanged?.Invoke(BuildTrayText());
    }

    private string BuildTrayText()
    {
        var parts = Headlines.Where(h => h.Value is not null)
            .Select(h => $"{(h.Title == "Procesor" ? "CPU" : "GPU")} {h.Text}")
            .ToList();
        var fan = Fans.FirstOrDefault(f => f.Percent is not null);
        if (fan is not null) parts.Add($"větrák {fan.PercentText}");
        var text = "Fan Control" + (parts.Count > 0 ? "\n" + string.Join(" · ", parts) : "");
        return text.Length > 127 ? text[..127] : text;
    }

    private static int Order(HardwareType t) => t switch
    {
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => 0,
        HardwareType.Cpu => 1,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => 2,
        HardwareType.Cooler => 3,
        _ => 9,
    };

    private static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(action);
    }
}
