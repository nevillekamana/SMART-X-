using System.Diagnostics;
using SmartX.Models.Streaming;

namespace SmartX.Services.Mesh;

public record HistoryStats(int Count, double? Min, double? Avg, double? Max, double? First, double? Last);

public record HistoryTreeStats(int Size, int Height, int OptimalHeight, long Rotations, int NodesVisited, double ElapsedMicros);

public record HistoryThresholds(double? CriticalLow, double? CriticalHigh, string Unit, string Metric, bool IsStateDevice);

public class HistoryQueryResult
{
    public string Mac { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Matched { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public HistoryStats Stats { get; set; } = new(0, null, null, null, null, null);
    public List<ReadingPoint> Items { get; set; } = new();       // the requested page, newest first
    public List<ReadingPoint> Series { get; set; } = new();      // chart series, oldest first, ≤ 240 points
    public HistoryTreeStats Tree { get; set; } = new(0, 0, 0, 0, 0, 0);
    public HistoryThresholds Thresholds { get; set; } = new(null, null, "", "", false);
}

/// <summary>
/// The historical archive for the Network Topology page: ONE AVL tree PER DEVICE, keyed by the reading's timestamp ticks.
/// "All moisture readings between 14:00 and 16:00 for Node A" becomes a pruned range walk over Node A's tree —
/// it never touches readings outside the window and never sorts anything.
/// Capped at 50,000 readings per device: when full, the minimum key (oldest reading) is evicted.
/// </summary>
public class TelemetryHistoryIndex
{
    public const int MaxPerDevice = 50_000;
    public const int MaxChartPoints = 240;

    private readonly object _gate = new();
    private readonly Dictionary<string, AvlTree<long, ReadingPoint>> _trees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optimal (perfectly packed) tree height for n nodes: ⌈log₂(n+1)⌉.</summary>
    public static int OptimalHeight(int n) => n <= 0 ? 0 : (int)Math.Ceiling(Math.Log2(n + 1));

    private static long KeyOf(DateTime t) => (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).Ticks;

    // ─── Writes ─────────────────────────────────────────────────────
    public void Add(string mac, ReadingPoint point)
    {
        lock (_gate)
        {
            if (!_trees.TryGetValue(mac, out var tree)) _trees[mac] = tree = new AvlTree<long, ReadingPoint>();

            long key = KeyOf(point.Timestamp);
            while (tree.TryGetValue(key, out _)) key++;              // two packets in the same tick: bump the key
            tree.Insert(key, point);

            if (tree.Count > MaxPerDevice && tree.TryGetMin(out var oldest, out _))
                tree.Remove(oldest);                                 // evict the oldest reading
        }
    }

    public int Count(string mac) { lock (_gate) return _trees.TryGetValue(mac, out var t) ? t.Count : 0; }

    // ─── Range query ────────────────────────────────────────────────
    public HistoryQueryResult Query(string mac, DateTime? from, DateTime? to, int page, int pageSize, DeviceView? meta = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(1, page);

        var matches = new List<ReadingPoint>();
        int visited = 0, size = 0, height = 0;
        long rotations = 0;
        double micros = 0;

        lock (_gate)
        {
            if (_trees.TryGetValue(mac, out var tree))
            {
                long lo = from.HasValue ? KeyOf(from.Value) : long.MinValue;
                long hi = to.HasValue ? KeyOf(to.Value) : long.MaxValue;
                var sw = Stopwatch.StartNew();
                visited = tree.VisitRange(lo, hi, (_, v) => matches.Add(v));   // ascending order
                sw.Stop();
                micros = Math.Round(sw.Elapsed.TotalMilliseconds * 1000, 1);
                size = tree.Count; height = tree.Height; rotations = tree.Rotations;
            }
        }

        var r = new HistoryQueryResult
        {
            Mac = mac, Name = meta?.Name ?? mac, From = from, To = to, Matched = matches.Count,
            PageSize = pageSize,
            TotalPages = Math.Max(1, (int)Math.Ceiling(matches.Count / (double)pageSize)),
            Stats = ComputeStats(matches),
            Tree = new HistoryTreeStats(size, height, OptimalHeight(size), rotations, visited, micros),
            Thresholds = new HistoryThresholds(meta?.CriticalLow, meta?.CriticalHigh, meta?.Unit ?? "", meta?.Metric ?? "",
                meta is not null && meta.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase))
        };
        r.Page = Math.Min(page, r.TotalPages);

        // newest-first page: walk the ascending list from the end
        int startFromEnd = (r.Page - 1) * pageSize;
        for (int i = matches.Count - 1 - startFromEnd, n = 0; i >= 0 && n < pageSize; i--, n++) r.Items.Add(matches[i]);

        r.Series = Decimate(matches);
        return r;
    }

    private static HistoryStats ComputeStats(List<ReadingPoint> m)
    {
        if (m.Count == 0) return new HistoryStats(0, null, null, null, null, null);
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var p in m) { if (p.Value < min) min = p.Value; if (p.Value > max) max = p.Value; sum += p.Value; }
        return new HistoryStats(m.Count, Math.Round(min, 2), Math.Round(sum / m.Count, 2), Math.Round(max, 2),
            Math.Round(m[0].Value, 2), Math.Round(m[^1].Value, 2));
    }

    /// <summary>
    /// Min/max decimation: split the window into ≤120 buckets and keep each bucket's lowest and highest reading,
    /// in time order. The chart stays ≤ 240 points but a one-sample spike can never disappear.
    /// </summary>
    private static List<ReadingPoint> Decimate(List<ReadingPoint> m)
    {
        if (m.Count <= MaxChartPoints) return new List<ReadingPoint>(m);
        const int buckets = MaxChartPoints / 2;
        var result = new List<ReadingPoint>(MaxChartPoints);
        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)b * m.Count / buckets);
            int end = (int)((long)(b + 1) * m.Count / buckets);       // exclusive
            if (end <= start) continue;
            int iMin = start, iMax = start;
            for (int i = start + 1; i < end; i++)
            {
                if (m[i].Value < m[iMin].Value) iMin = i;
                if (m[i].Value > m[iMax].Value) iMax = i;
            }
            if (iMin == iMax) result.Add(m[iMin]);
            else { result.Add(m[Math.Min(iMin, iMax)]); result.Add(m[Math.Max(iMin, iMax)]); }
        }
        return result;
    }

    // ─── Archive-wide stats (GET /api/mesh/history/stats) ───────────
    public object Stats()
    {
        lock (_gate)
        {
            var devices = _trees.Select(kv => new
            {
                mac = kv.Key, size = kv.Value.Count, height = kv.Value.Height,
                optimalHeight = OptimalHeight(kv.Value.Count), rotations = kv.Value.Rotations,
                valid = kv.Value.IsValid(),
                oldest = kv.Value.TryGetMin(out _, out var a) ? a.Timestamp : (DateTime?)null,
                newest = kv.Value.TryGetMax(out _, out var b) ? b.Timestamp : (DateTime?)null
            }).OrderBy(d => d.mac).ToList();
            return new
            {
                structure = "AVL tree per device (key = timestamp ticks)",
                capPerDevice = MaxPerDevice,
                devices = devices.Count,
                totalReadings = devices.Sum(d => d.size),
                allTreesValid = devices.All(d => d.valid),
                perDevice = devices
            };
        }
    }

    /// <summary>Internal — benchmark/test access to the full ascending key list for one device.</summary>
    public List<ReadingPoint> AllAscending(string mac)
    {
        lock (_gate)
            return _trees.TryGetValue(mac, out var t) ? t.InOrder().Select(kv => kv.Value).ToList() : new List<ReadingPoint>();
    }
}
