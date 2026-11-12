using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace InfoHubLite;

/// <summary>
/// cpu.load / ram.load come from two cheap Win32 calls. Everything else goes through LibreHardwareMonitor, which is
/// only loaded when a selected sensor needs it and only refreshes the hardware that owns a selected sensor.
/// </summary>
sealed class Sensors : IDisposable
{
    Lhm? _lhm;
    long _idle, _total;
    float? _cpu, _ram;

    static bool Native(string id) => id is "cpu.load" or "ram.load";

    public void Select(IReadOnlyCollection<string> ids)
    {
        _lhm?.Dispose(); _lhm = null;
        var external = ids.Where(i => !Native(i)).ToHashSet();
        if (external.Count > 0)
            try { _lhm = new Lhm(external); } catch (Exception) { /* library or driver unavailable: those elements show "--" */ }
        Update();
    }

    public void Update()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user)) // kernel time includes idle time
        {
            long total = kernel + user, dTotal = total - _total, dIdle = idle - _idle;
            if (_total != 0 && dTotal > 0) _cpu = Math.Clamp(100f * (dTotal - dIdle) / dTotal, 0, 100);
            _idle = idle; _total = total;
        }
        var m = new MEMORYSTATUSEX { dwLength = 64 };
        if (GlobalMemoryStatusEx(ref m)) _ram = m.dwMemoryLoad;
        try { _lhm?.Update(); } catch (Exception) { }
    }

    public (float? value, Kind kind) Read(string id) => id switch
    {
        "cpu.load" => (_cpu, Kind.Load),
        "ram.load" => (_ram, Kind.Load),
        _ => _lhm?.Read(id) ?? (null, Lhm.AliasKind(id)),
    };

    /// <summary>Everything this machine exposes, for the settings UI's picker ("InfoHubLite sensors").</summary>
    public static IEnumerable<(string id, string hardware, string name, Kind kind, float? value)> ListAll()
    {
        var native = new Sensors();
        native.Update();
        using var lhm = new Lhm(null);
        Thread.Sleep(1000); // load/clock sensors need two samples
        native.Update(); lhm.Update();
        yield return ("cpu.load", "System", "CPU Usage", Kind.Load, native._cpu);
        yield return ("ram.load", "System", "Memory Usage", Kind.Load, native._ram);
        foreach (var s in lhm.All()) yield return s;
    }

    public void Dispose() => _lhm?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong a, b, c, d, e, f, g; } // 64 bytes
    [DllImport("kernel32")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}

/// <summary>Kept in its own class so LibreHardwareMonitorLib.dll is not even loaded unless an element uses it.</summary>
sealed class Lhm : IDisposable
{
    /// <summary>Portable names the built-in templates use; resolved to whatever this machine has.</summary>
    public static readonly string[] Aliases = ["gpu.load", "gpu.temp", "cpu.temp"];

    readonly Computer _pc;
    readonly Dictionary<string, ISensor> _map = [];
    readonly HashSet<IHardware> _owners = [];
    readonly List<(ISensor sensor, IHardware hw)> _all = [];

    /// <param name="ids">sensor identifiers or aliases to track, or null to track everything</param>
    public Lhm(IReadOnlyCollection<string>? ids)
    {
        bool Need(string part) => ids == null || ids.Any(i => i.Contains(part, StringComparison.OrdinalIgnoreCase));
        bool known = ids != null && ids.All(i => i.Contains("cpu") || i.Contains("gpu") || i.StartsWith("/lpc") || i.StartsWith("/motherboard") || i.StartsWith("/ram") || i.StartsWith("/memory"));
        _pc = new Computer
        {
            IsCpuEnabled = !known || Need("cpu"),
            IsGpuEnabled = !known || Need("gpu"),
            IsMotherboardEnabled = !known || Need("/lpc") || Need("/motherboard"),
            IsMemoryEnabled = !known || Need("/ram") || Need("/memory"),
            IsControllerEnabled = !known,
        };
        _pc.Open();
        foreach (var hw in _pc.Hardware) Walk(hw);

        foreach (var (s, hw) in _all)
        {
            string id = s.Identifier.ToString();
            if (ids == null || ids.Contains(id)) { _map[id] = s; _owners.Add(hw); }
        }
        foreach (string alias in Aliases)
            if ((ids == null || ids.Contains(alias)) && Resolve(alias) is var (s, hw)) { _map[alias] = s; _owners.Add(hw); }
        if (ids != null) _all.Clear(); // only the listing needs the full set
    }

    void Walk(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors) _all.Add((s, hw));
        foreach (var sub in hw.SubHardware) Walk(sub);
    }

    public static Kind AliasKind(string id) => id.EndsWith(".temp") ? Kind.Temperature : id.EndsWith(".load") ? Kind.Load : Kind.Other;

    /// <summary>Best match for an alias: discrete GPUs before integrated, the headline sensor before per-core ones.</summary>
    (ISensor, IHardware)? Resolve(string alias)
    {
        bool gpu = alias.StartsWith("gpu");
        var type = alias.EndsWith(".temp") ? SensorType.Temperature : SensorType.Load;
        int Rank((ISensor s, IHardware hw) x)
        {
            int r = gpu ? x.hw.HardwareType switch { HardwareType.GpuNvidia => 0, HardwareType.GpuAmd => 10, _ => 20 } : 0;
            string n = x.s.Name;
            if (gpu) r += n == "GPU Core" ? 0 : n.Contains("3D") ? 1 : 5;
            else r += n.Contains("Tctl") || n.Contains("Package") ? 0 : n.Contains("Average") ? 1 : 5;
            return r;
        }
        var hit = _all.Where(x => x.sensor.SensorType == type && (gpu
                ? x.hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                : x.hw.HardwareType == HardwareType.Cpu))
            .OrderBy(Rank).ToList();
        return hit.Count > 0 ? hit[0] : null;
    }

    public void Update() { foreach (var hw in _owners) hw.Update(); }

    public (float?, Kind)? Read(string id) => _map.TryGetValue(id, out var s) ? (Clean(s), ToKind(s.SensorType)) : null;

    /// <summary>Without the hardware driver (no elevation) temperatures come back as exactly 0; that is "no reading", not 0 degrees.</summary>
    static float? Clean(ISensor s) => s.SensorType == SensorType.Temperature && s.Value is 0f ? null : s.Value;

    public IEnumerable<(string, string, string, Kind, float?)> All()
    {
        foreach (string alias in Aliases)
            if (_map.TryGetValue(alias, out var s)) yield return (alias, s.Hardware.Name, s.Name, ToKind(s.SensorType), Clean(s));
        foreach (var (s, hw) in _all)
            if (ToKind(s.SensorType) != Kind.Other) yield return (s.Identifier.ToString(), hw.Name, s.Name, ToKind(s.SensorType), Clean(s));
    }

    static Kind ToKind(SensorType t) => t switch
    {
        SensorType.Load => Kind.Load,
        SensorType.Temperature => Kind.Temperature,
        SensorType.Fan => Kind.Fan,
        SensorType.Clock => Kind.Clock,
        SensorType.Voltage => Kind.Voltage,
        SensorType.Power => Kind.Power,
        _ => Kind.Other,
    };

    public void Dispose() => _pc.Close();
}
