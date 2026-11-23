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

    public FanViewModel(FanChannelInfo info, FanSettings settings, ObservableCollection<SensorOption> temperatureOptions, Action<FanViewModel> changed)
    {
        Info = info;
        _settings = settings;
        _changed = changed;
        TemperatureOptions = temperatureOptions;

        if (_settings.SourceSensorId is null || temperatureOptions.All(o => o.Id != _settings.SourceSensorId))
        {
            _settings.SourceSensorId = info.SuggestedSourceId ?? temperatureOptions.FirstOrDefault()?.Id;
        }
        if (_settings.Curve is null || _settings.Curve.Count < 2)
        {
            _settings.Curve = CurvePresets.Balanced();
        }

        _mode = _settings.Mode;
        _manualPercent = _settings.ManualPercent;
        _hysteresis = _settings.Hysteresis;
        _selectedSource = temperatureOptions.FirstOrDefault(o => o.Id == _settings.SourceSensorId);
        _curvePoints = _settings.Curve;
        _editName = Name;
    }

    public FanChannelInfo Info { get; }
    public string Id => Info.Id;
    public string Name => string.IsNullOrWhiteSpace(_settings.CustomName) ? Info.DefaultName : _settings.CustomName!;
    public string HardwareName => Info.HardwareName;
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

    [ObservableProperty]
    private SensorOption? _selectedSource;

    partial void OnSelectedSourceChanged(SensorOption? value)
    {
        _settings.SourceSensorId = value?.Id;
        _changed(this);
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
