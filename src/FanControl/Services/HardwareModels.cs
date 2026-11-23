using FanControl.Models;
using LibreHardwareMonitor.Hardware;

namespace FanControl.Services;

/// <summary>Neměnné pravidlo pro jeden větrák – UI ho vytváří a služba hardwaru ho jen čte.</summary>
public sealed record FanRule(
    FanMode Mode,
    double ManualPercent,
    string? SourceSensorId,
    IReadOnlyList<CurvePoint> Curve,
    double Hysteresis)
{
    public static FanRule FromSettings(FanSettings s) => new(
        s.Mode,
        s.ManualPercent,
        s.SourceSensorId,
        s.Curve.Select(p => p.Clone()).OrderBy(p => p.Temperature).ToArray(),
        s.Hysteresis);
}

/// <summary>Ovladatelný kanál větráku (např. „GPU Fan 1“ nebo „Fan #2“ na základní desce).</summary>
public sealed record FanChannelInfo(
    string Id,
    string DefaultName,
    string HardwareName,
    HardwareType HardwareType,
    string HardwareId,
    string? RpmSensorId,
    string? SuggestedSourceId);

public sealed record SensorInfo(
    string Id,
    string Name,
    string HardwareName,
    HardwareType HardwareType,
    SensorType SensorType);

public readonly record struct SensorReading(float? Value, float? Min, float? Max);

public sealed record FanChannelState(
    float? Percent,
    float? Rpm,
    double? SourceTemperature,
    bool IsSoftwareControlled,
    string? Problem);

public sealed record HardwareSnapshot(
    IReadOnlyDictionary<string, SensorReading> Sensors,
    IReadOnlyDictionary<string, FanChannelState> Channels);

public sealed record HardwareEntry(string Name, HardwareType Type);

public sealed record DiscoveryResult(
    IReadOnlyList<FanChannelInfo> Channels,
    IReadOnlyList<SensorInfo> Sensors,
    IReadOnlyList<HardwareEntry> Hardware);

public static class CurveMath
{
    /// <summary>Lineární interpolace křivky; mimo rozsah drží krajní hodnoty.</summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> curve, double temperature)
    {
        if (curve.Count == 0) return 100;
        if (temperature <= curve[0].Temperature) return curve[0].Percent;
        for (var i = 1; i < curve.Count; i++)
        {
            var a = curve[i - 1];
            var b = curve[i];
            if (temperature <= b.Temperature)
            {
                var span = b.Temperature - a.Temperature;
                if (span <= 0) return b.Percent;
                return a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / span;
            }
        }
        return curve[^1].Percent;
    }
}
