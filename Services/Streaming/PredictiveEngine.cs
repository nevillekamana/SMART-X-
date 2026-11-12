using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

public enum EventKind { Alert, Search, Command }

/// <summary>One thing that happened on the console: an alert fired, the operator searched, or the operator ran a command.</summary>
public record OperatorEvent(DateTime Time, EventKind Kind, string Subject, string Detail)
{
    /// <summary>Identity used by the transition model, e.g. "ALERT|MoistureLow|Moisture Sensor B".</summary>
    public string Token => Kind switch
    {
        EventKind.Alert => $"ALERT|{Detail}|{Subject}",
        EventKind.Search => $"SEARCH|{Subject}",
        _ => $"CMD|{Subject}|{Detail}"
    };
}

/// <summary>
/// Predictive Action &amp; Recommendation Engine.
///
/// Four cooperating algorithms:
///  1. TRANSITION MODEL (Markov chain). Every alert / search / command is an event token. Each time a token
///     appears within 3 events (and 5 minutes) after another, the edge prev→next is counted once.
///     P(next | prev) = times next followed prev ÷ times prev occurred.
///  2. BEST-FIRST LOOKAHEAD. From each "trigger" (a currently active alert, or the operator's last action) a
///     PriorityQueue expands the most probable paths first (depth ≤ 3). Paths that end in a command become
///     "Suggested Actions"; the path probability is the confidence shown to the operator.
///  3. TREND FORECASTING. Least-squares regression over the latest readings predicts when a value will cross
///     its critical threshold, so the operator is warned before the alert actually fires.
///  4. RISK RANKING. A bounded min-heap (PriorityQueue of size K) keeps the K riskiest devices in O(n log K).
/// A z-score check on the newest reading adds "unusual spike" insights.
///
/// Data structures: Queue&lt;OperatorEvent&gt; ×2 (200-event query history + 3-event look-back window), Dictionary (transition
/// graph), PriorityQueue (lookahead + risk heap), HashSet. Suggestions are generated asynchronously (Task.Run + Task.WhenAll).
/// </summary>
public class PredictiveEngine
{
    private const int MaxLookback = 3;
    /// <summary>How many operator events the query-history Queue&lt;T&gt; keeps before it dequeues the oldest.</summary>
    public const int QueryHistoryCapacity = 200;
    private const int MaxDepth = 3;
    private const int MinSupport = 2;
    private const double MinConfidence = 0.25;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private sealed class Edge { public int Count; }

    private readonly object _gate = new();
    private readonly DeviceRegistry _registry;

    private readonly Dictionary<string, Dictionary<string, Edge>> _next = new();   // token → (next token → edge)
    private readonly Dictionary<string, int> _tokenCount = new();                   // token → times it occurred
    private readonly Dictionary<string, int> _searchCounts = new(StringComparer.OrdinalIgnoreCase);

    // Two explicit bounded FIFO queues (enqueue newest, dequeue oldest once full):
    //  • _queryHistory — the operator's last 200 events (alerts, searches, commands), for the history view
    //  • _window       — the 3-event look-back the Markov model links each new event to
    private readonly Queue<OperatorEvent> _queryHistory = new(QueryHistoryCapacity + 1);
    private readonly Queue<OperatorEvent> _window = new(MaxLookback + 1);
    private OperatorEvent? _lastEvent;
    private int _eventCount;
    private int _seededEvents;

    public PredictiveEngine(DeviceRegistry registry) { _registry = registry; }

    // ═══ Learning ═══════════════════════════════════════════════════
    public void RecordAlert(string deviceName, string alertType) => Record(new OperatorEvent(DateTime.UtcNow, EventKind.Alert, deviceName, alertType));
    public void RecordSearch(string deviceName) => Record(new OperatorEvent(DateTime.UtcNow, EventKind.Search, deviceName, ""));
    public void RecordCommand(string subject, string action) => Record(new OperatorEvent(DateTime.UtcNow, EventKind.Command, subject, action));

    /// <summary>Bootstrap the model with a historical operator log (used by the demo seeder).</summary>
    public void Train(IEnumerable<OperatorEvent> history)
    {
        foreach (var e in history.OrderBy(h => h.Time))
        {
            Record(e);
            lock (_gate) _seededEvents++;
        }
    }

    private void Record(OperatorEvent e)
    {
        lock (_gate)
        {
            _eventCount++;
            if (e.Kind == EventKind.Search)
                _searchCounts[e.Subject] = _searchCounts.GetValueOrDefault(e.Subject) + 1;

            _tokenCount[e.Token] = _tokenCount.GetValueOrDefault(e.Token) + 1;

            // Walk the look-back window newest → oldest. Queue<T> enumerates oldest → newest, so reverse a tiny array (≤3).
            var counted = new HashSet<string>();          // each earlier token links to this event at most once
            var lookback = _window.ToArray();
            for (int k = lookback.Length - 1; k >= 0; k--)
            {
                var prev = lookback[k];
                var dt = e.Time - prev.Time;
                if (dt <= TimeSpan.Zero || dt > Window) break;
                if (prev.Token != e.Token && counted.Add(prev.Token)) AddEdge(prev.Token, e.Token);
            }

            EnqueueBounded(_window, e, MaxLookback);
            EnqueueBounded(_queryHistory, e, QueryHistoryCapacity);
            _lastEvent = e;
        }
    }

    /// <summary>FIFO with a ceiling: Enqueue the newest event, and Dequeue the oldest when the queue is over capacity.</summary>
    private static void EnqueueBounded(Queue<OperatorEvent> queue, OperatorEvent e, int capacity)
    {
        queue.Enqueue(e);
        while (queue.Count > capacity) queue.Dequeue();
    }

    /// <summary>The most recent operator events, newest first, read from the bounded query-history queue.</summary>
    public List<OperatorEvent> RecentQueries(int take)
    {
        lock (_gate)
        {
            take = Math.Clamp(take, 1, QueryHistoryCapacity);
            var all = _queryHistory.ToArray();                       // oldest → newest
            var result = new List<OperatorEvent>(Math.Min(take, all.Length));
            for (int i = all.Length - 1; i >= 0 && result.Count < take; i--) result.Add(all[i]);
            return result;
        }
    }

    public (int Depth, int Capacity) QueryHistoryStats() { lock (_gate) return (_queryHistory.Count, QueryHistoryCapacity); }

    private void AddEdge(string from, string to)
    {
        if (!_next.TryGetValue(from, out var edges)) _next[from] = edges = new Dictionary<string, Edge>();
        if (!edges.TryGetValue(to, out var edge)) edges[to] = edge = new Edge();
        edge.Count++;
    }

    // ═══ Insights ═══════════════════════════════════════════════════
    private sealed record SuggestionHalf(List<SuggestedAction> Suggested, List<FrequentSearch> Frequent, object Model);
    private sealed record AnalysisHalf(List<Insight> Insights, List<DeviceRisk> AtRisk);

    /// <summary>
    /// Builds the dashboard insights asynchronously. One registry snapshot is taken, then two independent halves run
    /// in parallel on the thread pool and are awaited with Task.WhenAll:
    ///   A) BuildSuggestions — Markov transition model + best-first lookahead (reads the model under its lock)
    ///   B) AnalyseDevices   — trend forecast, z-score anomaly and the bounded risk heap (reads only the snapshot)
    /// The cancellation token is checked between items so an abandoned request stops doing work.
    /// Learning (Record*) stays synchronous — it is a few dictionary updates and must keep its event order.
    /// </summary>
    public async Task<InsightsResult> GetInsightsAsync(IReadOnlyList<AlertRecord> activeAlerts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var devices = _registry.Snapshot();                       // single snapshot shared by both halves

        var suggestionsTask = Task.Run(() => BuildSuggestions(activeAlerts, devices, ct), ct);
        var analysisTask = Task.Run(() => AnalyseDevices(activeAlerts, devices, ct), ct);
        await Task.WhenAll(suggestionsTask, analysisTask);

        ct.ThrowIfCancellationRequested();
        var a = await suggestionsTask;
        var b = await analysisTask;
        return new InsightsResult        // merge
        {
            SuggestedActions = a.Suggested,
            FrequentSearches = a.Frequent,
            Model = a.Model,
            Insights = b.Insights,
            AtRisk = b.AtRisk
        };
    }

    private SuggestionHalf BuildSuggestions(IReadOnlyList<AlertRecord> activeAlerts, List<DeviceView> devices, CancellationToken ct)
    {
        var byName = new Dictionary<string, DeviceView>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) byName.TryAdd(d.Name, d);

        var suggestions = new Dictionary<string, SuggestedAction>();
        object model;
        List<FrequentSearch> frequent;
        lock (_gate)
        {
            var triggers = new List<(string Token, string Label)>();
            foreach (var a in activeAlerts)
                triggers.Add(($"ALERT|{a.Type}|{a.DeviceName}", $"{a.DeviceName}: {AlertRules.Humanise(a.Type)}"));

            var last = _lastEvent;
            if (last is not null && DateTime.UtcNow - last.Time <= Window)
                triggers.Add((last.Token, DescribeEvent(last)));

            foreach (var (token, label) in triggers)
            {
                ct.ThrowIfCancellationRequested();
                Expand(token, label, byName, suggestions);
            }

            model = new
            {
                events = _eventCount,
                seededEvents = _seededEvents,
                transitions = _next.Values.Sum(e => e.Count),
                states = _next.Count,
                windowMinutes = (int)Window.TotalMinutes,
                lookback = MaxLookback,
                minSupport = MinSupport,
                queryHistoryDepth = _queryHistory.Count,
                queryHistoryCapacity = QueryHistoryCapacity
            };

            frequent = _searchCounts
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Take(4).Select(kv => new FrequentSearch(kv.Key, kv.Value)).ToList();
        }
        var top = suggestions.Values.OrderByDescending(s => s.Confidence).Take(5).ToList();
        return new SuggestionHalf(top, frequent, model);
    }

    private AnalysisHalf AnalyseDevices(IReadOnlyList<AlertRecord> activeAlerts, List<DeviceView> devices, CancellationToken ct)
    {
        var insights = new List<Insight>();
        var activeKeys = new HashSet<string>(activeAlerts.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        var heap = new PriorityQueue<DeviceRisk, double>();     // min-heap: smallest score at the root
        const int topK = 5;

        foreach (var d in devices)
        {
            ct.ThrowIfCancellationRequested();                   // checked between items
            double score = 0;
            var why = new List<string>();
            var mine = activeAlerts.Where(a => a.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var a in mine)
            {
                score += a.Severity == "Critical" ? 50 : 15;
                why.Add(AlertRules.Humanise(a.Type));
            }

            if (!d.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase))
            {
                var breach = PredictBreach(d, activeKeys);
                if (breach is not null) { insights.Add(breach); score += 25; why.Add("trending to threshold"); }

                var anomaly = DetectAnomaly(d, mine.Count > 0);
                if (anomaly is not null) { insights.Add(anomaly); score += 15; why.Add("unusual spike"); }
            }

            if (d.Signal is int sig && sig < -75)            // early warning before the -80 dBm alert fires
            {
                score += Math.Min(10, (-75 - sig) / 1.5);
                why.Add("weak signal");
            }

            if (why.Count > 0)
            {
                heap.Enqueue(new DeviceRisk(d.Mac, d.Name, Math.Round(score, 1), string.Join(", ", why.Distinct())), score);
                if (heap.Count > topK) heap.Dequeue();         // evict the least risky
            }
        }

        var atRisk = new List<DeviceRisk>();
        while (heap.Count > 0) atRisk.Add(heap.Dequeue());
        atRisk.Reverse();                                      // riskiest first
        insights = insights.OrderByDescending(i => i.Severity == "critical" ? 2 : 1).ThenByDescending(i => i.Confidence).ToList();
        return new AnalysisHalf(insights, atRisk);
    }

    // ─── Best-first lookahead over the transition graph ─────────────
    private void Expand(string trigger, string triggerLabel, Dictionary<string, DeviceView> byName, Dictionary<string, SuggestedAction> output)
    {
        var pq = new PriorityQueue<(List<string> Path, double Prob, int Support), double>();
        pq.Enqueue((new List<string> { trigger }, 1.0, int.MaxValue), -1.0);   // most probable path first
        int expansions = 0;

        while (pq.Count > 0 && expansions++ < 300)
        {
            var (path, prob, support) = pq.Dequeue();
            if (path.Count > 1) Emit(path, prob, support, triggerLabel, byName, output);
            if (path.Count > MaxDepth) continue;
            if (!_next.TryGetValue(path[^1], out var edges)) continue;

            // The trigger is an occurrence still in progress (no outcome yet), so it is not part of the denominator.
            double total = Math.Max(1, _tokenCount.GetValueOrDefault(path[^1]) - (path.Count == 1 ? 1 : 0));
            foreach (var (token, edge) in edges)
            {
                if (path.Contains(token) || edge.Count < MinSupport) continue;
                double p = prob * Math.Min(1.0, edge.Count / total);
                if (p < MinConfidence) continue;
                var extended = new List<string>(path) { token };
                pq.Enqueue((extended, p, Math.Min(support, edge.Count)), -p);
            }
        }
    }

    private static void Emit(List<string> path, double prob, int support, string triggerLabel,
        Dictionary<string, DeviceView> byName, Dictionary<string, SuggestedAction> output)
    {
        var parts = path[^1].Split('|');
        SuggestedAction? s = null;

        if (parts[0] == "CMD" && parts.Length == 3 && byName.TryGetValue(parts[1], out var dev)
            && dev.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase))
        {
            bool wantOn = parts[2].Equals("On", StringComparison.OrdinalIgnoreCase);
            if (dev.IsOn == wantOn) return;                   // already in that state — nothing to suggest
            s = new SuggestedAction
            {
                Id = $"cmd|{dev.Mac}|{parts[2]}",
                Kind = "RunCommand",
                Title = $"Turn {dev.Name} {(wantOn ? "ON" : "OFF")}",
                DeviceMac = dev.Mac,
                DeviceName = dev.Name,
                Action = wantOn ? "On" : "Off"
            };
        }
        else if (parts[0] == "SEARCH" && parts.Length == 2 && byName.TryGetValue(parts[1], out var target)
                 && !triggerLabel.StartsWith(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            s = new SuggestedAction
            {
                Id = $"inspect|{target.Mac}",
                Kind = "Inspect",
                Title = $"Inspect {target.Name}",
                DeviceMac = target.Mac,
                DeviceName = target.Name
            };
        }

        if (s is null) return;
        s.Confidence = Math.Round(prob, 2);
        s.Evidence = support == int.MaxValue ? 0 : support;
        s.Reason = $"{triggerLabel}. In {s.Evidence} earlier incident{(s.Evidence == 1 ? "" : "s")}, operators went on to do this next.";

        if (!output.TryGetValue(s.Id, out var existing) || existing.Confidence < s.Confidence)
            output[s.Id] = s;
    }

    private static string DescribeEvent(OperatorEvent e) => e.Kind switch
    {
        EventKind.Alert => $"{e.Subject}: {AlertRules.Humanise(e.Detail)}",
        EventKind.Search => $"You searched for {e.Subject}",
        _ => $"You ran {e.Detail} on {e.Subject}"
    };

    // ─── Trend forecasting (least-squares regression) ───────────────
    private static (double Slope, double R2, double Last, double Span)? Fit(List<ReadingPoint> points, int take)
    {
        var p = points.Skip(Math.Max(0, points.Count - take)).ToList();
        if (p.Count < 6) return null;

        var t0 = p[0].Timestamp;
        var xs = p.Select(q => (q.Timestamp - t0).TotalSeconds).ToArray();
        var ys = p.Select(q => q.Value).ToArray();
        if (xs[^1] - xs[0] < 4) return null;

        double mx = xs.Average(), my = ys.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            sxx += (xs[i] - mx) * (xs[i] - mx);
            sxy += (xs[i] - mx) * (ys[i] - my);
            syy += (ys[i] - my) * (ys[i] - my);
        }
        if (sxx < 1e-9 || syy < 1e-9) return null;
        return (sxy / sxx, (sxy * sxy) / (sxx * syy), ys[^1], xs[^1] - xs[0]);
    }

    private static Insight? PredictBreach(DeviceView d, HashSet<string> activeKeys)
    {
        var fit = Fit(d.RecentPoints, 12);
        if (fit is null || fit.Value.R2 < 0.8) return null;       // only strong, steady trends
        var (slope, r2, last, span) = fit.Value;
        double moved = Math.Abs(slope) * span;                    // how far the value travelled inside the window

        string? type = null;
        double? eta = null;
        double threshold = 0;

        if (d.CriticalLow is double low && slope < 0 && last > low)
        {
            type = d.Metric == "moisture" ? "MoistureLow" : "Low";
            threshold = low;
            eta = (last - low) / -slope;
            if (moved < 0.25 * (last - low)) return null;         // a small wobble far from the limit is not a trend
        }
        else if (d.CriticalHigh is double high && slope > 0 && last < high)
        {
            type = d.Metric == "power" ? "PowerSpike" : "OverTemp";
            threshold = high;
            eta = (high - last) / slope;
            if (moved < 0.25 * (high - last)) return null;
        }

        if (type is null || eta is null || eta > 600) return null;
        if (activeKeys.Contains($"{d.Mac}|{type}")) return null;

        var etaText = eta < 90 ? $"{Math.Max(1, (int)Math.Round(eta.Value))} s" : $"{eta.Value / 60:0.#} min";
        return new Insight
        {
            Id = $"breach|{d.Mac}",
            Kind = "PredictedBreach",
            Severity = eta < 60 ? "critical" : "warn",
            Title = $"{d.Name} heading for its limit",
            Detail = $"Moving {(slope < 0 ? "down" : "up")} about {Math.Abs(slope) * 60:0.#}{d.Unit}/min — projected to cross {threshold}{d.Unit} in ~{etaText}.",
            DeviceMac = d.Mac,
            Confidence = Math.Round(r2, 2)
        };
    }

    // ─── Anomaly detection (z-score of the newest reading) ──────────
    private static Insight? DetectAnomaly(DeviceView d, bool alreadyAlerting)
    {
        if (alreadyAlerting || d.RecentPoints.Count < 10) return null;

        var baseline = d.RecentPoints.Take(d.RecentPoints.Count - 1).Select(p => p.Value).ToArray();
        double mean = baseline.Average();
        double sd = Math.Sqrt(baseline.Sum(v => (v - mean) * (v - mean)) / baseline.Length);
        sd = Math.Max(sd, 0.02 * Math.Abs(mean) + 0.01);       // floor so a flat line doesn't flag every wobble

        double latest = d.RecentPoints[^1].Value;
        double z = (latest - mean) / sd;
        if (Math.Abs(z) < 3.5) return null;

        return new Insight
        {
            Id = $"anomaly|{d.Mac}",
            Kind = "Anomaly",
            Severity = "warn",
            Title = $"Unusual reading on {d.Name}",
            Detail = $"{latest:0.#}{d.Unit} is {Math.Abs(z):0.#}σ {(z > 0 ? "above" : "below")} its recent average of {mean:0.#}{d.Unit}.",
            DeviceMac = d.Mac,
            Confidence = Math.Round(Math.Min(0.99, Math.Abs(z) / 8.0), 2)
        };
    }
}
