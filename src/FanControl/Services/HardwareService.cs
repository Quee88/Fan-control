using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using FanControl.Models;
using LibreHardwareMonitor.Hardware;

namespace FanControl.Services;

/// <summary>
/// Komunikace s hardwarem přes LibreHardwareMonitor. Veškerý přístup k hardwaru běží na jednom
/// vlákně na pozadí, UI dostává jen hotové snímky hodnot přes události.
/// </summary>
public sealed class HardwareService : IDisposable
{
    // Rychlost změny při řízení křivkou (% za sekundu) – nahoru rychle, dolů pozvolna, ať větrák „necuká“.
    private const double RampUpPerSecond = 20;
    private const double RampDownPerSecond = 6;
    // I když se cílová hodnota nemění, občas ji zopakujeme – některé ovladače si řízení „vezmou zpět“.
    private static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, FanRule> _rules = new();
    private readonly Dictionary<string, ChannelRuntime> _channels = new();
    private readonly Dictionary<string, ISensor> _sensors = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private Computer? _computer;
    private Thread? _thread;
    private volatile int _intervalMs = 1000;
    private int _ticks;
    private bool _disposed;

    public event Action<DiscoveryResult>? Discovered;
    public event Action<HardwareSnapshot>? Updated;
    public event Action<string>? Failed;

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 250, 5000);
    }

    public void SetRule(string channelId, FanRule rule) => _rules[channelId] = rule;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Hardware", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            DiscoveryResult discovery;
            SystemInfo.EnsurePawnIoRunning();
            lock (_sync)
            {
                var sw = Stopwatch.StartNew();
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsStorageEnabled = true,
                    IsMemoryEnabled = true,
                    IsNetworkEnabled = true,
                    IsPsuEnabled = true,
                    IsBatteryEnabled = true,
                    IsPowerMonitorEnabled = true,
                };
                _computer.Open();
                UpdateAll();
                discovery = Discover();
                Log.Info($"Hardware načten za {sw.ElapsedMilliseconds} ms; " +
                         $"ovladatelných větráků: {discovery.Channels.Count}, senzorů: {discovery.Sensors.Count}.");
            }
            Discovered?.Invoke(discovery);
        }
        catch (Exception ex)
        {
            Log.Error("Inicializace hardwaru selhala.", ex);
            Failed?.Invoke(ex.Message);
            return;
        }

        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        while (!_stop.IsSet)
        {
            HardwareSnapshot snapshot;
            lock (_sync)
            {
                if (_disposed) return;
                var now = clock.Elapsed;
                var dt = Math.Clamp((now - last).TotalSeconds, 0.05, 5);
                last = now;

                try { UpdateAll(); }
                catch (Exception ex) { Log.Error("Čtení senzorů selhalo.", ex); }

                foreach (var channel in _channels.Values)
                {
                    var previousProblem = channel.Problem;
                    Control(channel, dt, now);
                    if (channel.Problem != previousProblem)
                        Log.Info($"{channel.Info.DefaultName}: {channel.Problem ?? "řízení opět v pořádku"}");
                }
                snapshot = BuildSnapshot();

                if (++_ticks == 5)
                {
                    // Diagnostika: zapíše, které teploty se po rozběhu skutečně čtou.
                    var missing = _sensors.Values.Where(s => s.SensorType == SensorType.Temperature && s.Value is null)
                        .Select(s => $"{s.Hardware.Name}/{s.Name}").ToList();
                    Log.Info(missing.Count == 0 ? "Všechny teploty se čtou." : "Teploty bez hodnoty: " + string.Join(", ", missing));
                }
            }

            try { Updated?.Invoke(snapshot); }
            catch (Exception ex) { Log.Error("Chyba při zpracování snímku.", ex); }

            _stop.Wait(_intervalMs);
        }
    }

    private void UpdateAll()
    {
        if (_computer is null) return;
        foreach (var hw in _computer.Hardware)
        {
            UpdateRecursive(hw);
        }

        static void UpdateRecursive(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
        }
    }

    private DiscoveryResult Discover()
    {
        var all = new List<(IHardware Hw, IHardware Root)>();
        foreach (var hw in _computer!.Hardware) Collect(hw, hw, all);

        var sensors = new List<SensorInfo>();
        var channels = new List<FanChannelInfo>();
        var hardwareNames = _computer.Hardware.Select(h => new HardwareEntry(h.Name, h.HardwareType)).ToList();

        var activeNics = ActiveNetworkInterfaceIds();
        foreach (var (hw, root) in all)
        {
            // Windows hlásí desítky virtuálních adaptérů a filtrů (QoS, WFP, Wi-Fi Direct…) –
            // ukážeme jen síťová připojení, která jsou opravdu aktivní.
            if (root.HardwareType == HardwareType.Network && !activeNics.Contains(NicId(root))) continue;

            var counts = hw.Sensors.GroupBy(s => s.SensorType).Select(g => $"{g.Key}={g.Count()}");
            Log.Info($"  {hw.HardwareType} \"{hw.Name}\" ({hw.Identifier}): {string.Join(", ", counts)}");
            foreach (var s in hw.Sensors)
            {
                var id = s.Identifier.ToString();
                if (!_sensors.TryAdd(id, s)) continue;
                sensors.Add(new SensorInfo(id, s.Name, hw.Name, root.HardwareType, s.SensorType));
            }
        }

        foreach (var (hw, root) in all)
        {
            var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null))
            {
                var id = s.Identifier.ToString();
                // Otáčky patří ke stejnému indexu, jinak zkusíme pořadí.
                var rpm = fans.FirstOrDefault(f => f.Index == s.Index)
                          ?? fans.ElementAtOrDefault(hw.Sensors.Where(x => x.SensorType == SensorType.Control).ToList().IndexOf(s));
                var info = new FanChannelInfo(
                    id,
                    FriendlyName(s, root),
                    hw.Name,
                    root.HardwareType,
                    root.Identifier.ToString(),
                    rpm?.Identifier.ToString(),
                    SuggestSource(root));
                channels.Add(info);
                _channels[id] = new ChannelRuntime(info, s.Control!, s, rpm);
            }
        }

        return new DiscoveryResult(channels, sensors, hardwareNames);

        static void Collect(IHardware hw, IHardware root, List<(IHardware, IHardware)> list)
        {
            list.Add((hw, root));
            foreach (var sub in hw.SubHardware) Collect(sub, root, list);
        }
    }

    private static HashSet<string> ActiveNetworkInterfaceIds()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            // Instance filtrů mají v názvu příponu „-0000“.
                            && !Regex.IsMatch(n.Name, @"-\d{4}$"))
                .Select(n => n.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Seznam síťových adaptérů nelze načíst.", ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Z identifikátoru „/nic/%7BGUID%7D“ vrátí „{GUID}“.</summary>
    private static string NicId(IHardware hw)
    {
        var id = Uri.UnescapeDataString(hw.Identifier.ToString());
        return id[(id.LastIndexOf('/') + 1)..];
    }

    private string? SuggestSource(IHardware root)
    {
        // GPU větrák → teplota jádra téže karty. Ostatní → teplota CPU.
        if (root.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            var gpu = root.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
            return (gpu.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? gpu.FirstOrDefault())
                ?.Identifier.ToString();
        }

        var cpu = _computer!.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu is null) return null;
        var temps = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
        return (temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                ?? temps.FirstOrDefault(s => s.Name.Contains("Max", StringComparison.OrdinalIgnoreCase))
                ?? temps.FirstOrDefault())
            ?.Identifier.ToString();
    }

    private static string FriendlyName(ISensor s, IHardware root)
    {
        var name = s.Name;
        if (root.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            var digits = new string(name.Where(char.IsDigit).ToArray());
            return string.IsNullOrEmpty(digits) ? "Větrák grafické karty" : $"Větrák grafické karty {digits}";
        }
        return name.Replace("Fan Control", "Větrák", StringComparison.OrdinalIgnoreCase)
                   .Replace("Fan", "Větrák", StringComparison.OrdinalIgnoreCase);
    }

    private void Control(ChannelRuntime ch, double dt, TimeSpan now)
    {
        if (!_rules.TryGetValue(ch.Info.Id, out var rule))
        {
            rule = new FanRule(FanMode.Auto, 50, [], [], 0);
        }

        ch.Problem = null;
        ch.SourceTemperature = null;
        ch.SourceSensorId = null;
        try
        {
            double target;
            switch (rule.Mode)
            {
                case FanMode.Manual:
                    target = rule.ManualPercent;
                    ch.Output = target;
                    break;

                case FanMode.Curve:
                    if (!TryReadHottest(rule.SourceSensorIds, out var temp, out var hottestId, out var problem))
                    {
                        // Bezpečnost: bez platné teploty vracíme řízení systému.
                        ch.Problem = problem;
                        ReleaseToDefault(ch);
                        return;
                    }

                    ch.SourceTemperature = temp;
                    ch.SourceSensorId = hottestId;
                    if (ch.HysteresisTemp is null || temp > ch.HysteresisTemp || temp <= ch.HysteresisTemp - rule.Hysteresis)
                    {
                        ch.HysteresisTemp = temp;
                    }
                    target = CurveMath.Evaluate(rule.Curve, ch.HysteresisTemp.Value);

                    if (ch.Output is null || !ch.IsSoftware)
                    {
                        ch.Output = target;
                    }
                    else
                    {
                        var current = ch.Output.Value;
                        ch.Output = target > current
                            ? Math.Min(target, current + RampUpPerSecond * dt)
                            : Math.Max(target, current - RampDownPerSecond * dt);
                    }
                    break;

                default:
                    ch.HysteresisTemp = null;
                    ReleaseToDefault(ch);
                    return;
            }

            var value = (float)Math.Round(Math.Clamp(ch.Output!.Value, ch.Control.MinSoftwareValue, ch.Control.MaxSoftwareValue));
            var changed = ch.LastApplied is null || Math.Abs(ch.LastApplied.Value - value) >= 1;
            if (!ch.IsSoftware || changed || now - ch.LastApplyTime > ReapplyInterval)
            {
                ch.Control.SetSoftware(value);
                ch.IsSoftware = true;
                ch.LastApplied = value;
                ch.LastApplyTime = now;
            }
        }
        catch (Exception ex)
        {
            ch.Problem = "Větrák nelze ovládat: " + ex.Message;
            Log.Error($"Řízení {ch.Info.Id} selhalo.", ex);
            ReleaseToDefault(ch);
        }
    }

    /// <summary>
    /// Najde nejvyšší z vybraných teplot. Když kterákoli z nich chybí, vrací false – raději vrátíme
    /// řízení systému, než abychom větrák řídili jen podle zbylých (třeba chladnějších) senzorů.
    /// </summary>
    private bool TryReadHottest(IReadOnlyList<string> ids, out double temperature, out string? hottestId, out string problem)
    {
        temperature = double.MinValue;
        hottestId = null;
        problem = "";
        if (ids.Count == 0)
        {
            problem = "Není vybraná žádná teplota – řízení bylo vráceno systému.";
            return false;
        }

        foreach (var id in ids)
        {
            _sensors.TryGetValue(id, out var sensor);
            var value = sensor?.Value;
            if (value is null or <= 0 or > 150)
            {
                var name = sensor is null ? "vybraná teplota" : $"{sensor.Name} ({sensor.Hardware.Name.Trim()})";
                problem = $"Teplota {name} není dostupná – řízení bylo vráceno systému.";
                return false;
            }
            if (value.Value > temperature)
            {
                temperature = value.Value;
                hottestId = id;
            }
        }
        return true;
    }

    private static void ReleaseToDefault(ChannelRuntime ch)
    {
        ch.Output = null;
        ch.LastApplied = null;
        if (!ch.IsSoftware) return;
        try { ch.Control.SetDefault(); }
        catch (Exception ex) { Log.Error($"Vrácení {ch.Info.Id} do výchozího režimu selhalo.", ex); }
        ch.IsSoftware = false;
    }

    private HardwareSnapshot BuildSnapshot()
    {
        var sensors = new Dictionary<string, SensorReading>(_sensors.Count);
        foreach (var (id, s) in _sensors)
        {
            sensors[id] = new SensorReading(s.Value, s.Min, s.Max);
        }

        var channels = new Dictionary<string, FanChannelState>(_channels.Count);
        foreach (var (id, ch) in _channels)
        {
            channels[id] = new FanChannelState(
                ch.ControlSensor.Value,
                ch.RpmSensor?.Value,
                ch.SourceTemperature,
                ch.SourceSensorId,
                ch.IsSoftware,
                ch.Problem);
        }
        return new HardwareSnapshot(sensors, channels);
    }

    /// <summary>Vrátí všechny větráky do řízení systému. Volá se při ukončení i při pádu.</summary>
    public void RestoreDefaults()
    {
        if (!Monitor.TryEnter(_sync, TimeSpan.FromSeconds(3))) return;
        try
        {
            foreach (var ch in _channels.Values)
            {
                if (!ch.IsSoftware) continue;
                try { ch.Control.SetDefault(); ch.IsSoftware = false; }
                catch (Exception ex) { Log.Error($"Obnovení {ch.Info.Id} selhalo.", ex); }
            }
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        RestoreDefaults();
        lock (_sync)
        {
            _disposed = true;
            try { _computer?.Close(); }
            catch (Exception ex) { Log.Error("Uzavření hardwaru selhalo.", ex); }
        }
    }

    private sealed class ChannelRuntime(FanChannelInfo info, IControl control, ISensor controlSensor, ISensor? rpm)
    {
        public FanChannelInfo Info { get; } = info;
        public IControl Control { get; } = control;
        public ISensor ControlSensor { get; } = controlSensor;
        public ISensor? RpmSensor { get; } = rpm;
        public bool IsSoftware { get; set; }
        public double? Output { get; set; }
        public float? LastApplied { get; set; }
        public TimeSpan LastApplyTime { get; set; }
        public double? HysteresisTemp { get; set; }
        public double? SourceTemperature { get; set; }
        public string? SourceSensorId { get; set; }
        public string? Problem { get; set; }
    }
}
