using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FanControl.Models;
using FanControl.Services;

namespace FanControl.ViewModels;

public sealed record SensorOption(string Id, string Display);

public sealed partial class FanViewModel : ObservableObject
{
    private readonly FanSettings _settings;
    private readonly Action<FanViewModel> _changed;
    private readonly bool _initialized;

    public FanViewModel(FanChannelInfo info, FanSettings settings, ObservableCollection<SensorOption> temperatureOptions, Action<FanViewModel> changed)
    {
        Info = info;
        _settings = settings;
        _changed = changed;
        TemperatureOptions = temperatureOptions;

        _settings.Normalize();

        // Zdroje teploty: jen ty, které na tomhle PC opravdu existují; když nezbude žádný, navrhneme.
        var sources = _settings.SourceSensorIds
            .Select(id => temperatureOptions.FirstOrDefault(o => o.Id == id))
            .OfType<SensorOption>()
            .ToList();
        if (sources.Count == 0)
        {
            var suggested = temperatureOptions.FirstOrDefault(o => o.Id == info.SuggestedSourceId) ?? temperatureOptions.FirstOrDefault();
            if (suggested is not null) sources.Add(suggested);
        }
        foreach (var option in sources) Sources.Add(new SourceSlotViewModel(this, option));
        _settings.SourceSensorIds = sources.Select(o => o.Id).ToList();
        RefreshSlots();

        _mode = _settings.Mode;
        _manualPercent = _settings.ManualPercent;
        _hysteresis = _settings.Hysteresis;
        _curvePoints = _settings.Curve;
        _initialized = true;
        _editName = Name;
    }

    public FanChannelInfo Info { get; }
    public string Id => Info.Id;
    public string Name => string.IsNullOrWhiteSpace(_settings.CustomName) ? Info.DefaultName : _settings.CustomName!;
    public string HardwareName => Info.HardwareName;
    public string? CustomName => _settings.CustomName;
    public bool IsGpu => Info.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia
        or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel;
    public ObservableCollection<SensorOption> TemperatureOptions { get; }

    public FanRule ToRule() => FanRule.FromSettings(_settings);

    // ---------- Režim ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuto), nameof(IsManual), nameof(IsCurve), nameof(ModeDescription))]
    private FanMode _mode;

    partial void OnModeChanged(FanMode value)
    {
        _settings.Mode = value;
        _changed(this);
    }

    public bool IsAuto
    {
        get => Mode == FanMode.Auto;
        set { if (value) Mode = FanMode.Auto; }
    }

    public bool IsManual
    {
        get => Mode == FanMode.Manual;
        set { if (value) Mode = FanMode.Manual; }
    }

    public bool IsCurve
    {
        get => Mode == FanMode.Curve;
        set { if (value) Mode = FanMode.Curve; }
    }

    public string ModeDescription => Mode switch
    {
        FanMode.Manual => "Větrák běží stále stejnou rychlostí, kterou nastavíš posuvníkem.",
        FanMode.Curve => "Rychlost se mění podle teploty. Body v grafu přetáhni myší, dvojklikem přidáš bod, pravým tlačítkem ho odebereš.",
        _ => IsGpu
            ? "Rychlost řídí ovladač grafické karty – stejně, jako kdyby aplikace neběžela."
            : "Rychlost řídí BIOS základní desky – stejně, jako kdyby aplikace neběžela.",
    };

    // ---------- Ruční režim ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualPercentText))]
    private double _manualPercent;

    partial void OnManualPercentChanged(double value)
    {
        _settings.ManualPercent = Math.Round(value);
        _changed(this);
    }

    public string ManualPercentText => $"{Math.Round(ManualPercent)} %";

    [RelayCommand]
    private void SetManual(string percent)
    {
        ManualPercent = double.Parse(percent, CultureInfo.InvariantCulture);
    }

    // ---------- Křivka ----------

    /// <summary>Vybrané teploty; větrák se řídí podle nejvyšší z nich.</summary>
    public ObservableCollection<SourceSlotViewModel> Sources { get; } = [];

    public bool HasMultipleSources => Sources.Count > 1;
    public bool CanAddSource => Sources.Count < TemperatureOptions.Count;

    [RelayCommand]
    private void AddSource()
    {
        var used = Sources.Select(s => s.Selected?.Id).ToHashSet();
        var next = TemperatureOptions.FirstOrDefault(o => !used.Contains(o.Id));
        if (next is null) return;
        Sources.Add(new SourceSlotViewModel(this, next));
        RefreshSlots();
        SourcesEdited();
    }

    internal void RemoveSource(SourceSlotViewModel slot)
    {
        if (Sources.Count <= 1) return;
        Sources.Remove(slot);
        RefreshSlots();
        SourcesEdited();
    }

    internal void SourcesEdited()
    {
        if (!_initialized) return;
        _settings.SourceSensorIds = Sources.Select(s => s.Selected?.Id).OfType<string>().Distinct().ToList();
        _changed(this);
    }

    private void RefreshSlots()
    {
        foreach (var slot in Sources) slot.CanRemove = Sources.Count > 1;
        OnPropertyChanged(nameof(HasMultipleSources));
        OnPropertyChanged(nameof(CanAddSource));
    }

    /// <summary>Živé hodnoty vybraných teplot – i když větrák zrovna běží v jiném režimu.</summary>
    public void ApplyLive(IReadOnlyDictionary<string, SensorReading> sensors)
    {
        double? max = null;
        SourceSlotViewModel? hottest = null;
        foreach (var slot in Sources)
        {
            slot.LiveValue = slot.Selected is { } o && sensors.TryGetValue(o.Id, out var r) ? r.Value : null;
            if (slot.LiveValue is { } v && (max is null || v > max))
            {
                max = v;
                hottest = slot;
            }
        }
        foreach (var slot in Sources) slot.IsHottest = HasMultipleSources && ReferenceEquals(slot, hottest);
        LiveSourceTemperature = max;
    }

    [ObservableProperty]
    private List<CurvePoint> _curvePoints;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HysteresisText))]
    private double _hysteresis;

    partial void OnHysteresisChanged(double value)
    {
        _settings.Hysteresis = Math.Round(value);
        _changed(this);
    }

    public string HysteresisText => $"{Math.Round(Hysteresis)} °C";

    /// <summary>Volá editor křivky po každé úpravě bodů.</summary>
    [RelayCommand]
    private void CurveEdited()
    {
        _settings.Curve = CurvePoints;
        _changed(this);
    }

    [RelayCommand]
    private void ApplyPreset(string preset)
    {
        CurvePoints = preset switch
        {
            "silent" => CurvePresets.Silent(),
            "performance" => CurvePresets.Performance(),
            _ => CurvePresets.Balanced(),
        };
        _settings.Curve = CurvePoints;
        _changed(this);
    }

    // ---------- Přejmenování ----------

    [ObservableProperty]
    private bool _isEditingName;

    [ObservableProperty]
    private string _editName;

    [RelayCommand]
    private void StartRename()
    {
        EditName = Name;
        IsEditingName = true;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        var name = EditName?.Trim();
        _settings.CustomName = string.IsNullOrEmpty(name) || name == Info.DefaultName ? null : name;
        IsEditingName = false;
        OnPropertyChanged(nameof(Name));
        _changed(this);
    }

    [RelayCommand]
    private void CancelRename() => IsEditingName = false;

    // ---------- Živé hodnoty ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText), nameof(PercentValue))]
    private float? _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpmText), nameof(HasRpm))]
    private float? _rpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceTemperatureText))]
    private double? _sourceTemperature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public string PercentText => Percent is { } p ? $"{p:0} %" : "—";
    public double PercentValue => Percent ?? 0;
    public bool HasRpm => Rpm is not null;
    public string RpmText => Rpm is { } r ? $"{r.ToString("N0", CultureInfo.GetCultureInfo("cs-CZ"))} ot./min" : "";
    public string SourceTemperatureText => SourceTemperature is { } t ? $"{t:0} °C" : "—";
    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public void Apply(FanChannelState state)
    {
        Percent = state.Percent;
        Rpm = state.Rpm;
        SourceTemperature = state.SourceTemperature;
        Problem = state.Problem;
    }

    /// <summary>Aktuální teplota zdroje – pro zobrazení v grafu i když větrák běží v jiném režimu.</summary>
    [ObservableProperty]
    private double? _liveSourceTemperature;
}

/// <summary>Jedna vybraná teplota v seznamu „Řídit podle“.</summary>
public sealed partial class SourceSlotViewModel : ObservableObject
{
    private readonly FanViewModel _owner;

    public SourceSlotViewModel(FanViewModel owner, SensorOption? selected)
    {
        _owner = owner;
        _selected = selected;
    }

    public ObservableCollection<SensorOption> Options => _owner.TemperatureOptions;

    [ObservableProperty]
    private SensorOption? _selected;

    partial void OnSelectedChanged(SensorOption? value) => _owner.SourcesEdited();

    [ObservableProperty] private bool _canRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private float? _liveValue;

    public string LiveText => LiveValue is { } v ? $"{v:0} °C" : "—";

    /// <summary>Tahle teplota je teď nejvyšší, takže podle ní větrák jede.</summary>
    [ObservableProperty] private bool _isHottest;

    [RelayCommand]
    private void Remove() => _owner.RemoveSource(this);
}
