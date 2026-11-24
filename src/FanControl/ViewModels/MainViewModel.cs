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
    private IReadOnlyList<FanChannelInfo> _channels = [];
    private HardwareSnapshot? _lastSnapshot;
    private bool _loadingSettings;

    public MainViewModel(HardwareService hardware, SettingsService settings)
    {
        _hardware = hardware;
        _settings = settings;

        IsAdministrator = SystemInfo.IsAdministrator();
        PawnIo = SystemInfo.GetPawnIoState();

        _loadingSettings = true;
        ThemeIndex = (int)settings.Current.Theme;
        StartWithWindows = settings.Current.StartWithWindows;
        StartMinimized = settings.Current.StartMinimized;
        CloseToTray = settings.Current.CloseToTray;
        UpdateIntervalSeconds = settings.Current.UpdateIntervalMs / 1000.0;
        foreach (var p in settings.Current.Profiles) Profiles.Add(new ProfileItemViewModel(p));
        ActiveProfile = Profiles.FirstOrDefault(p => p.Model.Id == settings.Current.ActiveProfileId) ?? Profiles.FirstOrDefault();
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
    /// <summary>Změnil se seznam profilů nebo aktivní profil (kvůli menu v oznamovací oblasti).</summary>
    public event Action? ProfilesChanged;

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
    [NotifyPropertyChangedFor(nameof(ShowPawnIoWarning), nameof(CanInstallPawnIo), nameof(PawnIoStatus),
                              nameof(PawnIoTitle), nameof(PawnIoMessage), nameof(PawnIoActionText))]
    private PawnIoState _pawnIo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIoStatus))]
    [NotifyCanExecuteChangedFor(nameof(InstallPawnIoCommand))]
    private bool _isInstallingPawnIo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPawnIoWarning), nameof(CanInstallPawnIo), nameof(PawnIoStatus),
                              nameof(PawnIoTitle), nameof(PawnIoMessage))]
    private bool _pawnIoNeedsRestart;

    public bool ShowPawnIoWarning => PawnIo != PawnIoState.Ready || PawnIoNeedsRestart;
    public bool CanInstallPawnIo => PawnIo != PawnIoState.Ready && !PawnIoNeedsRestart;

    public string PawnIoStatus => IsInstallingPawnIo ? "Instaluji…"
        : PawnIoNeedsRestart ? "Připraveno – restartuj aplikaci"
        : PawnIo switch
        {
            PawnIoState.Ready => "Funguje",
            PawnIoState.Broken => "Nainstalováno neúplně",
            _ => "Chybí",
        };

    public string PawnIoTitle => PawnIoNeedsRestart ? "Ovladač PawnIO je připravený"
        : PawnIo == PawnIoState.Broken ? "Ovladač PawnIO nefunguje"
        : "Chybí ovladač PawnIO";

    public string PawnIoMessage => PawnIoNeedsRestart
        ? "Aby ho aplikace začala používat, je potřeba ji restartovat."
        : PawnIo == PawnIoState.Broken
            ? "Ovladač je v systému, ale nainstalovaný jen napůl, takže se teplota procesoru nečte. " +
              "Oprava ho přeinstaluje oficiálním instalátorem."
            : "Je potřeba pro čtení teploty procesoru a řízení větráků na základní desce. Grafická karta funguje i bez něj.";

    public string PawnIoActionText => PawnIo == PawnIoState.Broken ? "Opravit PawnIO" : "Nainstalovat PawnIO";

    [RelayCommand(CanExecute = nameof(CanRunPawnIoInstall))]
    private async Task InstallPawnIo()
    {
        IsInstallingPawnIo = true;
        var state = await SystemInfo.InstallPawnIoAsync();
        IsInstallingPawnIo = false;
        PawnIo = state;
        if (state == PawnIoState.Ready)
            PawnIoNeedsRestart = true;
        else
            SystemInfo.OpenUrl("https://pawnio.eu/");
    }

    private bool CanRunPawnIoInstall() => !IsInstallingPawnIo;

    [RelayCommand]
    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        App.RestartRequested = true;
        Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        ((App)Application.Current).ExitApp();
    }

    // ---------- Profily ----------

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private ProfileItemViewModel? _activeProfile;

    partial void OnActiveProfileChanged(ProfileItemViewModel? value)
    {
        if (_loadingSettings || value is null) return;
        IsRenamingProfile = false;
        _settings.Current.ActiveProfileId = value.Model.Id;
        Log.Info($"Profil: {value.Name}");
        RebuildFans();
        _settings.SaveSoon();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Pro menu v oznamovací oblasti.</summary>
    public IReadOnlyList<(string Id, string Name, bool IsActive)> ProfileMenu =>
        Profiles.Select(p => (p.Model.Id, p.Name, ReferenceEquals(p, ActiveProfile))).ToList();

    public void SelectProfile(string id)
    {
        var profile = Profiles.FirstOrDefault(p => p.Model.Id == id);
        if (profile is not null) ActiveProfile = profile;
    }

    [ObservableProperty] private bool _isRenamingProfile;
    [ObservableProperty] private string _editProfileName = "";

    [RelayCommand]
    private void NewProfile()
    {
        var source = _settings.Current.GetActiveProfile();
        var model = new FanProfile
        {
            Name = UniqueProfileName("Můj profil"),
            Fans = source.Fans.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        };
        _settings.Current.Profiles.Add(model);
        var item = new ProfileItemViewModel(model);
        Profiles.Add(item);
        DeleteProfileCommand.NotifyCanExecuteChanged();
        ActiveProfile = item;
        StartRenameProfile();
    }

    [RelayCommand]
    private void StartRenameProfile()
    {
        if (ActiveProfile is null) return;
        EditProfileName = ActiveProfile.Name;
        IsRenamingProfile = true;
    }

    [RelayCommand]
    private void ConfirmRenameProfile()
    {
        var name = EditProfileName?.Trim();
        if (ActiveProfile is not null && !string.IsNullOrEmpty(name))
        {
            ActiveProfile.Name = name;
            _settings.SaveSoon();
            ProfilesChanged?.Invoke();
        }
        IsRenamingProfile = false;
    }

    [RelayCommand]
    private void CancelRenameProfile() => IsRenamingProfile = false;

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfile()
    {
        if (ActiveProfile is not { } doomed || Profiles.Count <= 1) return;
        var result = await new Wpf.Ui.Controls.MessageBox
        {
            Title = "Smazat profil",
            Content = $"Opravdu smazat profil „{doomed.Name}“? Jeho nastavení větráků se ztratí.",
            PrimaryButtonText = "Smazat",
            CloseButtonText = "Zrušit",
        }.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        _settings.Current.Profiles.Remove(doomed.Model);
        var index = Profiles.IndexOf(doomed);
        Profiles.Remove(doomed);
        ActiveProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        DeleteProfileCommand.NotifyCanExecuteChanged();
        _settings.SaveSoon();
        ProfilesChanged?.Invoke();
    }

    private bool CanDeleteProfile() => Profiles.Count > 1;

    private string UniqueProfileName(string baseName)
    {
        var names = Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!names.Contains(candidate)) return candidate;
        }
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
        // Vlákno hardwaru mezitím mohlo PawnIO spustit – stav ověříme znovu.
        if (!PawnIoNeedsRestart) PawnIo = SystemInfo.GetPawnIoState();

        HardwareSummary = string.Join(" · ", d.Hardware
            .Where(h => h.Type is HardwareType.Motherboard or HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .Select(h => h.Name.Trim())
            .Distinct());

        foreach (var s in d.Sensors.Where(s => s.SensorType == SensorType.Temperature)
                     .OrderBy(s => Order(s.HardwareType)).ThenBy(s => s.HardwareName))
        {
            TemperatureOptions.Add(new SensorOption(s.Id, $"{s.Name} · {s.HardwareName}"));
        }

        foreach (var group in d.Sensors.GroupBy(s => (s.HardwareName, s.HardwareType))
                     .OrderBy(g => Order(g.Key.HardwareType)).ThenBy(g => g.Key.HardwareName))
        {
            var vm = new SensorGroupViewModel(group.Key.HardwareName, group.Key.HardwareType);
            // OrderBy je stabilní – uvnitř typu zůstane pořadí z knihovny (Core #1, #2, … #10).
            foreach (var s in group.OrderBy(s => SensorFormat.Order(s.SensorType)))
            {
                var svm = new SensorViewModel(s);
                vm.Sensors.Add(svm);
                _sensorById[s.Id] = svm;
            }
            vm.ApplyFilter(SensorFilter);
            SensorGroups.Add(vm);
        }
        OnPropertyChanged(nameof(SensorCountText));

        AddHeadline(d, HardwareType.Cpu, SensorType.Temperature, "Procesor", ["CPU Package", "Package", "Core Max", "Core Average", "Tctl"], "CPU");
        foreach (var gpuType in new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel })
        {
            AddHeadline(d, gpuType, SensorType.Temperature, "Grafická karta", ["GPU Core", "Core", "Hot Spot"], "GPU");
        }
        AddHeadline(d, HardwareType.Cpu, SensorType.Load, "Vytížení CPU", ["CPU Total"], null);
        AddHeadline(d, HardwareType.Memory, SensorType.Load, "Paměť RAM", ["Memory"], "RAM");

        _channels = d.Channels.OrderBy(c => Order(c.HardwareType)).ToList();
        RebuildFans();

        IsLoading = false;
        _settings.SaveSoon();
    }

    /// <summary>Vytvoří karty větráků podle aktivního profilu (po načtení hardwaru a při přepnutí profilu).</summary>
    private void RebuildFans()
    {
        Fans.Clear();
        var profile = _settings.Current.GetActiveProfile();
        foreach (var ch in _channels)
        {
            var fs = profile.GetOrCreateFan(ch.Id);
            // Vlastní název větráku platí ve všech profilech.
            fs.CustomName ??= _settings.Current.Profiles
                .Select(p => p.Fans.GetValueOrDefault(ch.Id)?.CustomName)
                .FirstOrDefault(n => n is not null);
            var fan = new FanViewModel(ch, fs, TemperatureOptions, OnFanChanged);
            Fans.Add(fan);
            _hardware.SetRule(ch.Id, fan.ToRule());
            if (_lastSnapshot is not null) ApplySnapshot(fan, _lastSnapshot);
        }
        OnPropertyChanged(nameof(ShowNoFans));
    }

    private void AddHeadline(DiscoveryResult d, HardwareType hardware, SensorType type, string title, string[] preferred, string? trayLabel)
    {
        // Fyzická paměť má přednost před virtuální (stránkovací soubor).
        var candidates = d.Sensors.Where(s => s.HardwareType == hardware && s.SensorType == type)
            .OrderBy(s => s.HardwareName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return;
        var pick = preferred.Select(p => candidates.FirstOrDefault(t => t.Name.Equals(p, StringComparison.OrdinalIgnoreCase)))
                       .FirstOrDefault(t => t is not null)
                   ?? preferred.Select(p => candidates.FirstOrDefault(t => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                       .FirstOrDefault(t => t is not null)
                   ?? candidates[0];
        Headlines.Add(new HeadlineViewModel(title, pick.HardwareName.Trim(), pick.Id, type, trayLabel));
    }

    private void OnFanChanged(FanViewModel fan)
    {
        // Přejmenování platí pro všechny profily.
        foreach (var p in _settings.Current.Profiles)
        {
            if (p.Fans.TryGetValue(fan.Id, out var other)) other.CustomName = fan.CustomName;
        }
        _hardware.SetRule(fan.Id, fan.ToRule());
        _settings.SaveSoon();
    }

    private void OnUpdated(HardwareSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        foreach (var (id, reading) in snapshot.Sensors)
        {
            if (_sensorById.TryGetValue(id, out var vm)) vm.Apply(reading);
        }

        foreach (var h in Headlines)
        {
            h.Value = snapshot.Sensors.TryGetValue(h.SensorId, out var r) ? r.Value : null;
        }

        foreach (var fan in Fans) ApplySnapshot(fan, snapshot);

        TrayTextChanged?.Invoke(BuildTrayText());
    }

    private static void ApplySnapshot(FanViewModel fan, HardwareSnapshot snapshot)
    {
        if (snapshot.Channels.TryGetValue(fan.Id, out var state)) fan.Apply(state);
        fan.ApplyLive(snapshot.Sensors);
    }

    private string BuildTrayText()
    {
        var parts = Headlines.Where(h => h.TrayLabel is not null && h.Value is not null)
            .Select(h => $"{h.TrayLabel} {h.Text}")
            .ToList();
        var fan = Fans.FirstOrDefault(f => f.Percent is not null);
        if (fan is not null) parts.Add($"větrák {fan.PercentText}");
        var header = Profiles.Count > 1 && ActiveProfile is not null ? $"Fan Control – {ActiveProfile.Name}" : "Fan Control";
        var text = header + (parts.Count > 0 ? "\n" + string.Join(" · ", parts) : "");
        return text.Length > 127 ? text[..127] : text;
    }

    // ---------- Senzory ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSensorMatch))]
    private string _sensorFilter = "";

    partial void OnSensorFilterChanged(string value)
    {
        foreach (var g in SensorGroups) g.ApplyFilter(value);
        OnPropertyChanged(nameof(ShowNoSensorMatch));
    }

    public bool ShowNoSensorMatch => !string.IsNullOrWhiteSpace(SensorFilter) && SensorGroups.All(g => !g.IsVisible);

    public string SensorCountText => $"{_sensorById.Count} senzorů z {SensorGroups.Count} zařízení. Minimum a maximum platí od spuštění aplikace.";

    private static int Order(HardwareType t) => t switch
    {
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => 0,
        HardwareType.Cpu => 1,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => 2,
        HardwareType.Cooler => 3,
        HardwareType.Memory => 4,
        HardwareType.Storage => 5,
        HardwareType.Psu or HardwareType.Battery or HardwareType.PowerMonitor => 6,
        HardwareType.Network => 7,
        _ => 9,
    };

    private static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(action);
    }
}
