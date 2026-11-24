using System.Text.Json.Serialization;

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

    /// <summary>Teploty, podle kterých se řídí křivka – použije se nejvyšší z nich.</summary>
    public List<string> SourceSensorIds { get; set; } = [];

    /// <summary>Jediný zdroj z verze 0.1 – při načtení se převede do <see cref="SourceSensorIds"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSensorId { get; set; }

    public List<CurvePoint> Curve { get; set; } = CurvePresets.Balanced();
    /// <summary>O kolik °C musí teplota klesnout, než se větrák zpomalí (zabraňuje kolísání).</summary>
    public double Hysteresis { get; set; } = 3;

    public void Normalize()
    {
        SourceSensorIds ??= [];
        if (!string.IsNullOrEmpty(SourceSensorId) && !SourceSensorIds.Contains(SourceSensorId))
            SourceSensorIds.Insert(0, SourceSensorId);
        SourceSensorId = null;
        if (Curve is null || Curve.Count < 2) Curve = CurvePresets.Balanced();
    }

    public FanSettings Clone() => new()
    {
        CustomName = CustomName,
        Mode = Mode,
        ManualPercent = ManualPercent,
        SourceSensorIds = [.. SourceSensorIds],
        Curve = Curve.Select(p => p.Clone()).ToList(),
        Hysteresis = Hysteresis,
    };
}

/// <summary>Pojmenovaná sada nastavení všech větráků (např. Tichý / Normální / Výkonný).</summary>
public sealed class FanProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profil";

    /// <summary>
    /// Křivka pro větráky, které v profilu ještě nemají vlastní nastavení:
    /// „silent“ / „performance“ = řízení podle teploty s danou předvolbou, jinak automatika.
    /// </summary>
    public string? Preset { get; set; }

    public Dictionary<string, FanSettings> Fans { get; set; } = new();

    public FanSettings GetOrCreateFan(string channelId)
    {
        if (Fans.TryGetValue(channelId, out var fan)) return fan;
        fan = Preset switch
        {
            "silent" => new FanSettings { Mode = FanMode.Curve, Curve = CurvePresets.Silent() },
            "performance" => new FanSettings { Mode = FanMode.Curve, Curve = CurvePresets.Performance() },
            _ => new FanSettings(),
        };
        Fans[channelId] = fan;
        return fan;
    }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;

    public List<FanProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }

    /// <summary>Nastavení větráků z verze 0.1 (před profily) – při načtení se přesune do profilu „Normální“.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, FanSettings>? Fans { get; set; }

    public FanProfile GetActiveProfile() => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    /// <summary>Doplní chybějící hodnoty a převede nastavení ze starších verzí.</summary>
    public void Normalize()
    {
        Profiles ??= [];
        Profiles.RemoveAll(p => p is null);
        if (Profiles.Count == 0)
        {
            Profiles.Add(new FanProfile { Name = "Normální", Fans = Fans ?? new() });
            Profiles.Add(new FanProfile { Name = "Tichý", Preset = "silent" });
            Profiles.Add(new FanProfile { Name = "Výkonný", Preset = "performance" });
        }
        Fans = null;

        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "Profil";
            profile.Fans ??= new();
            foreach (var fan in profile.Fans.Values) fan?.Normalize();
        }

        if (Profiles.All(p => p.Id != ActiveProfileId)) ActiveProfileId = Profiles[0].Id;
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 250, 5000);
    }
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
