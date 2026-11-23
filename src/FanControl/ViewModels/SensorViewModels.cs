using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FanControl.Services;
using LibreHardwareMonitor.Hardware;

namespace FanControl.ViewModels;

public sealed partial class SensorViewModel(SensorInfo info) : ObservableObject
{
    private static readonly CultureInfo Cz = CultureInfo.GetCultureInfo("cs-CZ");

    public SensorInfo Info { get; } = info;
    public string Name => Info.Name;

    public string Kind => Info.SensorType switch
    {
        SensorType.Temperature => "Teplota",
        SensorType.Fan => "Otáčky",
        SensorType.Control => "Výkon větráku",
        _ => Info.SensorType.ToString(),
    };

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _min = "—";
    [ObservableProperty] private string _max = "—";

    public void Apply(SensorReading r)
    {
        Value = Format(r.Value);
        Min = Format(r.Min);
        Max = Format(r.Max);
    }

    private string Format(float? v) => v is not { } x
        ? "—"
        : Info.SensorType switch
        {
            SensorType.Temperature => $"{x:0.0} °C".Replace('.', ','),
            SensorType.Fan => $"{x.ToString("N0", Cz)} ot./min",
            SensorType.Control => $"{x:0} %",
            _ => x.ToString("0.##", Cz),
        };
}

public sealed class SensorGroupViewModel(string hardwareName, HardwareType type)
{
    public string HardwareName { get; } = hardwareName;
    public HardwareType HardwareType { get; } = type;

    public string Category => HardwareType switch
    {
        HardwareType.Cpu => "Procesor",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Grafická karta",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Základní deska",
        HardwareType.Storage => "Disk",
        HardwareType.Cooler => "Chladič",
        _ => HardwareType.ToString(),
    };

    public ObservableCollection<SensorViewModel> Sensors { get; } = [];
}

/// <summary>Dlaždice s klíčovou teplotou na přehledu (CPU, GPU).</summary>
public sealed partial class HeadlineViewModel(string title, string subtitle, string sensorId) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string SensorId { get; } = sensorId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private float? _value;

    public string Text => Value is { } v ? $"{v:0} °C" : "—";
}
