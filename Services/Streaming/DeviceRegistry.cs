using SmartX.Models;
using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

/// <summary>
/// Live device store for the Real-Time Command Stream.
///
///  • Dictionary&lt;string, LiveDevice&gt;  — keyed by MAC address, so when a telemetry packet arrives the
///    device is found in O(1) (no looping over every registered sensor).
///  • Dictionary&lt;string, SortedDictionary&lt;long, ReadingPoint&gt;&gt; — per-device history ordered by
///    timestamp, so the dashboard can draw a timeline without sorting on every request.
/// </summary>
public class DeviceRegistry
{
    private const int MaxHistoryPerDevice = 720;
    private const int MaxRecent = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, LiveDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<long, ReadingPoint>> _history = new(StringComparer.OrdinalIgnoreCase);
    private int _syncedSensorCount = -1;

    private sealed class LiveDevice
    {
        public string Mac = "";
        public string Id = "";
        public string Name = "";
        public string Kind = "";
        public string Category = "";
        public string Location = "";
        public string Metric = "";
        public double? LastValue;
        public int? Signal;
        public DateTime? LastSeen;
        public bool IsOn;
        public bool? ReportedOn;
        public long PacketCount;
        public readonly List<ReadingPoint> Recent = new();
    }

    public int Count { get { lock (_gate) return _devices.Count; } }

    // ─── Registration ───────────────────────────────────────────────
    public void EnsureFromSensor(SensorRegistration s, string? name = null, string? kind = null, string? metric = null)
    {
        lock (_gate)
        {
            if (_devices.ContainsKey(s.MacAddress)) return;
            var cat = s.Category;
            _devices[s.MacAddress] = new LiveDevice
            {
                Mac = s.MacAddress,
                Id = s.Id,
                Name = name ?? $"{s.Location} {DefaultKind(cat)}",
                Kind = kind ?? DefaultKind(cat),
                Category = cat,
                Location = s.Location,
                Metric = metric ?? AlertRules.DefaultMetricFor(cat)
            };
            _history[s.MacAddress] = new SortedDictionary<long, ReadingPoint>();
        }
    }

    private static string DefaultKind(string category) => category.ToLowerInvariant() switch
    {
        "environmental" => "Sensor",
        "powerconsumption" => "Meter",
        _ => "Relay"
    };

    /// <summary>Pull in any sensors registered through Part 1's form. Cheap no-op unless the count changed.</summary>
    public void SyncFrom(TelemetryService telemetry)
    {
        var count = telemetry.SensorCount;
        lock (_gate)
        {
            if (count == _syncedSensorCount) return;
        }
        foreach (var s in telemetry.GetAllSensors()) EnsureFromSensor(s);
        lock (_gate) { _syncedSensorCount = count; }
    }

    // ─── Lookups ────────────────────────────────────────────────────
    public bool Contains(string mac) { lock (_gate) return _devices.ContainsKey(mac); }

    /// <summary>O(1) lookup by MAC.</summary>
    public bool TryGetView(string mac, out DeviceView view)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(mac, out var d)) { view = ToView(d); return true; }
        }
        view = null!;
        return false;
    }

    public List<DeviceView> Snapshot()
    {
        lock (_gate) { return _devices.Values.Select(ToView).ToList(); }
    }

    public List<DeviceView> Actuators(string? kind)
    {
        lock (_gate)
        {
            return _devices.Values
                .Where(d => d.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase))
                .Where(d => string.IsNullOrEmpty(kind) || kind.Equals("All", StringComparison.OrdinalIgnoreCase)
                            || d.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Select(ToView).ToList();
        }
    }

    public List<(string Mac, string Name, DateTime LastSeen)> SeenDevices()
    {
        lock (_gate)
        {
            return _devices.Values.Where(d => d.LastSeen.HasValue)
                .Select(d => (d.Mac, d.Name, d.LastSeen!.Value)).ToList();
        }
    }

    // ─── Telemetry ──────────────────────────────────────────────────
    /// <summary>Apply a processed packet to its device. Returns null if the device is unknown.</summary>
    public DeviceView? ApplyReading(string mac, double numeric, bool? state, int signal, DateTime ts)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(mac, out var d)) return null;

            d.LastValue = numeric;
            d.Signal = signal;
            d.LastSeen = ts;
            d.PacketCount++;
            if (d.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase) && state.HasValue)
                d.ReportedOn = state.Value;

            var point = new ReadingPoint(ts, numeric, signal);
            d.Recent.Add(point);
            if (d.Recent.Count > MaxRecent) d.Recent.RemoveAt(0);

            AddToHistory(mac, point);
            return ToView(d);
        }
    }

    // SortedDictionary keyed by timestamp ticks. Two packets can land in the same tick, so bump on collision.
    private void AddToHistory(string mac, ReadingPoint point)
    {
        if (!_history.TryGetValue(mac, out var h))
        {
            h = new SortedDictionary<long, ReadingPoint>();
            _history[mac] = h;
        }
        var key = point.Timestamp.Ticks;
        while (h.ContainsKey(key)) key++;
        h[key] = point;

        if (h.Count > MaxHistoryPerDevice)
            h.Remove(h.Keys.First());       // oldest entry is always the first key
    }

    /// <summary>Chronological history (oldest → newest), last <paramref name="limit"/> points.</summary>
    public List<ReadingPoint> GetHistory(string mac, int limit)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(mac, out var h)) return new List<ReadingPoint>();
            var skip = Math.Max(0, h.Count - limit);
            return h.Values.Skip(skip).ToList();     // already sorted by the SortedDictionary
        }
    }

    public int HistoryCount(string mac) { lock (_gate) return _history.TryGetValue(mac, out var h) ? h.Count : 0; }

    /// <summary>Back-fill history before the first live packet (demo seeding). Does not mark the device as seen.</summary>
    public void SeedHistory(string mac, IEnumerable<ReadingPoint> points)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(mac, out var d)) return;
            foreach (var p in points)
            {
                AddToHistory(mac, p);
                d.Recent.Add(p);
                if (d.Recent.Count > MaxRecent) d.Recent.RemoveAt(0);
                d.LastValue = p.Value;
                d.Signal = p.Signal;
            }
        }
    }

    // ─── Actuators ──────────────────────────────────────────────────
    public bool TrySetActuator(string mac, bool on, out bool previous, out string name)
    {
        lock (_gate)
        {
            previous = false;
            name = "";
            if (!_devices.TryGetValue(mac, out var d)) return false;
            if (!d.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase)) return false;
            previous = d.IsOn;
            name = d.Name;
            d.IsOn = on;
            return true;
        }
    }

    // ─── Projection ─────────────────────────────────────────────────
    private static DeviceView ToView(LiveDevice d)
    {
        var (low, high) = AlertRules.ThresholdsFor(d.Metric);
        return new DeviceView
        {
            Mac = d.Mac,
            Id = d.Id,
            Name = d.Name,
            Kind = d.Kind,
            Category = d.Category,
            Location = d.Location,
            Metric = d.Metric,
            Unit = AlertRules.UnitFor(d.Metric),
            LastValue = d.LastValue,
            Signal = d.Signal,
            LastSeen = d.LastSeen,
            IsOn = d.IsOn,
            ReportedOn = d.ReportedOn,
            PacketCount = d.PacketCount,
            CriticalLow = low,
            CriticalHigh = high,
            Spark = d.Recent.Select(p => Math.Round(p.Value, 2)).ToArray(),
            RecentPoints = new List<ReadingPoint>(d.Recent)
        };
    }
}
