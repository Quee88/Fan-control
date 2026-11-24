using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FanControl.Services;
using LibreHardwareMonitor.Hardware;

namespace FanControl.ViewModels;

/// <summary>České názvy a formátování hodnot pro všechny typy senzorů knihovny.</summary>
public static class SensorFormat
{
    private static readonly CultureInfo Cz = CultureInfo.GetCultureInfo("cs-CZ");

    public static string Kind(SensorType type) => type switch
    {
        SensorType.Temperature => "Teplota",
        SensorType.Load => "Vytížení",
        SensorType.Clock => "Frekvence",
        SensorType.Frequency => "Frekvence",
        SensorType.Power => "Příkon",
        SensorType.Voltage => "Napětí",
        SensorType.Current => "Proud",
        SensorType.Energy => "Energie",
        SensorType.Fan => "Otáčky",
        SensorType.Control => "Výkon větráku",
        SensorType.Flow => "Průtok",
        SensorType.Level => "Úroveň",
        SensorType.Factor => "Poměr",
        SensorType.Data => "Data",
        SensorType.SmallData => "Data",
        SensorType.Throughput => "Přenos",
        SensorType.TimeSpan => "Doba",
        SensorType.Timing => "Časování",
        SensorType.Noise => "Hluk",
        SensorType.Conductivity => "Vodivost",
        SensorType.Humidity => "Vlhkost",
        _ => type.ToString(),
    };

    /// <summary>Pořadí v tabulce – nejzajímavější typy nahoře.</summary>
    public static int Order(SensorType type) => type switch
    {
        SensorType.Temperature => 0,
        SensorType.Fan => 1,
        SensorType.Control => 2,
        SensorType.Load => 3,
        SensorType.Clock => 4,
        SensorType.Power => 5,
        SensorType.Voltage => 6,
        SensorType.Current => 7,
        SensorType.Data => 8,
        SensorType.SmallData => 9,
        SensorType.Throughput => 10,
        _ => 20,
    };

    public static string Format(SensorType type, float? value)
    {
        if (value is not { } v || float.IsNaN(v) || float.IsInfinity(v)) return "—";
        return type switch
        {
            SensorType.Temperature => v.ToString("0.0", Cz) + " °C",
            SensorType.Load or SensorType.Level or SensorType.Humidity => v.ToString("0.0", Cz) + " %",
            SensorType.Control => v.ToString("0", Cz) + " %",
            SensorType.Clock => v.ToString("N0", Cz) + " MHz",
            SensorType.Frequency => v.ToString("N0", Cz) + " Hz",
            SensorType.Power => v.ToString("0.0", Cz) + " W",
            SensorType.Voltage => v.ToString("0.000", Cz) + " V",
            SensorType.Current => v.ToString("0.00", Cz) + " A",
            SensorType.Energy => v.ToString("N0", Cz) + " mWh",
            SensorType.Fan => v.ToString("N0", Cz) + " ot./min",
            SensorType.Flow => v.ToString("0.0", Cz) + " l/h",
            SensorType.Factor => v.ToString("0.00", Cz),
            SensorType.Data => v.ToString("N1", Cz) + " GB",
            SensorType.SmallData => v.ToString("N0", Cz) + " MB",
            SensorType.Throughput => Throughput(v),
            SensorType.TimeSpan => Duration(v),
            SensorType.Timing => v.ToString("0.0", Cz) + " ns",
            SensorType.Noise => v.ToString("0", Cz) + " dBA",
            SensorType.Conductivity => v.ToString("0.0", Cz) + " µS/cm",
            _ => v.ToString("0.##", Cz),
        };
    }

    private static string Throughput(float bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => bytesPerSecond.ToString("0", Cz) + " B/s",
        < 1024 * 1024 => (bytesPerSecond / 1024).ToString("0.0", Cz) + " kB/s",
        _ => (bytesPerSecond / 1024 / 1024).ToString("0.0", Cz) + " MB/s",
    };

    private static string Duration(float seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.ToString(@"h\:mm\:ss", Cz);
    }
}

public sealed partial class SensorViewModel(SensorInfo info) : ObservableObject
{
    public SensorInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Kind => SensorFormat.Kind(Info.SensorType);

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _min = "—";
    [ObservableProperty] private string _max = "—";
    [ObservableProperty] private bool _isVisible = true;

    public void Apply(SensorReading r)
    {
        Value = SensorFormat.Format(Info.SensorType, r.Value);
        Min = SensorFormat.Format(Info.SensorType, r.Min);
        Max = SensorFormat.Format(Info.SensorType, r.Max);
    }

    public bool Matches(string filter) =>
        Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Kind.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

public sealed partial class SensorGroupViewModel(string hardwareName, HardwareType type) : ObservableObject
{
    public string HardwareName { get; } = hardwareName;
    public HardwareType HardwareType { get; } = type;

    public string Category => HardwareType switch
    {
        HardwareType.Cpu => "Procesor",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Grafická karta",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Základní deska",
        HardwareType.Memory => "Paměť",
        HardwareType.Storage => "Disk",
        HardwareType.Network => "Síť",
        HardwareType.Cooler => "Chladič",
        HardwareType.Psu => "Zdroj",
        HardwareType.Battery => "Baterie",
        HardwareType.PowerMonitor => "Měření příkonu",
        _ => HardwareType.ToString(),
    };

    public ObservableCollection<SensorViewModel> Sensors { get; } = [];

    [ObservableProperty] private bool _isVisible = true;

    public void ApplyFilter(string? filter)
    {
        var text = filter?.Trim() ?? "";
        var groupMatches = text.Length == 0
                           || HardwareName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                           || Category.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        var any = false;
        foreach (var s in Sensors)
        {
            s.IsVisible = groupMatches || s.Matches(text);
            any |= s.IsVisible;
        }
        IsVisible = any;
    }
}

/// <summary>Dlaždice s klíčovou hodnotou na přehledu (teplota CPU a GPU, vytížení…).</summary>
public sealed partial class HeadlineViewModel(string title, string subtitle, string sensorId, SensorType type, string? trayLabel)
    : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string SensorId { get; } = sensorId;
    public SensorType Type { get; } = type;

    /// <summary>Krátký popisek do bubliny ikony u hodin; null = do bubliny nepatří.</summary>
    public string? TrayLabel { get; } = trayLabel;

    public bool IsTemperature => Type == SensorType.Temperature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private float? _value;

    public string Text => Value is not { } v ? "—"
        : Type switch
        {
            SensorType.Temperature => $"{v:0} °C",
            SensorType.Load => $"{v:0} %",
            SensorType.Power => $"{v:0} W",
            _ => SensorFormat.Format(Type, v),
        };
}
