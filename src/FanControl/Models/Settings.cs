namespace FanControl.Models;

public enum FanMode
{
    /// <summary>Řízení vrací ovladači GPU / BIOSu (výchozí chování systému).</summary>
    Auto,
    /// <summary>Pevná rychlost v procentech.</summary>
    Manual,
    /// <summary>Rychlost podle křivky teplota → otáčky.</summary>
    Curve,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class CurvePoint
{
    public CurvePoint() { }

    public CurvePoint(double temperature, double percent)
    {
        Temperature = temperature;
        Percent = percent;
    }

    public double Temperature { get; set; }
    public double Percent { get; set; }

    public CurvePoint Clone() => new(Temperature, Percent);
}

public sealed class FanSettings
{
    public string? CustomName { get; set; }
    public FanMode Mode { get; set; } = FanMode.Auto;
    public double ManualPercent { get; set; } = 50;
    public string? SourceSensorId { get; set; }
    public List<CurvePoint> Curve { get; set; } = CurvePresets.Balanced();
    /// <summary>O kolik °C musí teplota klesnout, než se větrák zpomalí (zabraňuje kolísání).</summary>
    public double Hysteresis { get; set; } = 3;
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;
    public Dictionary<string, FanSettings> Fans { get; set; } = new();
}

public static class CurvePresets
{
    public static List<CurvePoint> Silent() =>
    [
        new(30, 20), new(50, 30), new(65, 45), new(75, 65), new(85, 100),
    ];

    public static List<CurvePoint> Balanced() =>
    [
        new(30, 30), new(45, 40), new(60, 55), new(72, 75), new(82, 100),
    ];

    public static List<CurvePoint> Performance() =>
    [
        new(30, 40), new(45, 55), new(58, 70), new(68, 90), new(75, 100),
    ];
}
