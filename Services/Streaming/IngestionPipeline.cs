using SmartX.Models;
using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

/// <summary>
/// The heart of Part 2: how telemetry packets are queued, prioritised, processed and de-duplicated.
///
///  • Queue&lt;StreamPacket&gt;                         — standard FIFO lane for routine telemetry.
///  • PriorityQueue&lt;StreamPacket,(int,long)&gt;      — critical lane. A power spike or dry hydroponics node is
///                                                   dequeued BEFORE any standard packet, however long the
///                                                   standard queue is. Ties break by arrival order.
///  • HashSet&lt;string&gt; (_activeErrorStates)        — "MAC|AlertType" keys for every active error state or
///                                                   disconnected node. Add() returns false for a duplicate,
///                                                   so the same alert can never be raised twice.
/// The device lookup on every packet is an O(1) Dictionary hit in <see cref="DeviceRegistry"/>.
/// </summary>
public class IngestionPipeline
{
    private const int MaxStandardDepth = 5000;
    private const int MaxPriorityDepth = 1000;
    private const int MaxLog = 100;
    private const int MaxAlertLog = 200;

    private readonly object _gate = new();
    private readonly DeviceRegistry _registry;
    private readonly PredictiveEngine _engine;
    private readonly TelemetryService _telemetry;
    private readonly TimeSpan _forwardEvery;

    // ── Queues ──
    private readonly Queue<StreamPacket> _standard = new();
    private readonly PriorityQueue<StreamPacket, (int Urgency, long Seq)> _priority = new();

    // ── Unique active error states / disconnected nodes ──
    private readonly HashSet<string> _activeErrorStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AlertRecord> _alertDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlertRecord> _alertLog = new();
    private long _duplicatesSuppressed;

    // ── Counters ──
    private long _sequence, _processOrder, _ingested, _rejected;
    private long _processedStandard, _processedPriority;
    private double _latencyStandardEma, _latencyPriorityEma;
    private readonly Queue<ProcessedRecord> _processedLog = new();
    private readonly Queue<(DateTime At, long Count)> _throughputSamples = new();
    private readonly Dictionary<string, DateTime> _lastForward = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _paused;
    private long _resumedTicks;

    /// <summary>Operator pause. Time spent paused is not counted as queue latency.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused && !value) Interlocked.Exchange(ref _resumedTicks, DateTime.UtcNow.Ticks);
            _paused = value;
        }
    }

    public IngestionPipeline(DeviceRegistry registry, PredictiveEngine engine, TelemetryService telemetry, IConfiguration config)
    {
        _registry = registry;
        _engine = engine;
        _telemetry = telemetry;
        _forwardEvery = TimeSpan.FromSeconds(config.GetValue("Streaming:ForwardToPart1EverySeconds", 5));
    }

    // ═══ Device resolution ══════════════════════════════════════════
    /// <summary>Accepts a MAC or a Part 1 sensor id and returns the canonical MAC (or null).</summary>
    public string? ResolveMac(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        deviceId = deviceId.Trim();
        if (_registry.Contains(deviceId)) return deviceId;           // O(1)

        var s = _telemetry.GetSensor(deviceId);                      // O(1): Part 1 indexes by MAC and by id
        if (s is null) return null;
        _registry.EnsureFromSensor(s);
        return s.MacAddress;
    }

    // ═══ INGEST (called by the HTTP endpoint) ═══════════════════════
    /// <summary>Validate, classify and enqueue. Does no heavy work — the processor drains the queues.</summary>
    public IngestResult Ingest(IngestRequest req)
    {
        var mac = ResolveMac(req.DeviceId);
        if (mac is null)
            return Reject(404, "Unknown device. Register the sensor first (Part 1) or use one of the seeded devices.");
        if (!_registry.TryGetView(mac, out var dev))
            return Reject(404, "Unknown device.");

        var metric = (req.Metric ?? dev.Metric).Trim().ToLowerInvariant();
        if (!AlertRules.Metrics.Contains(metric))
            return Reject(400, $"Unknown metric '{metric}'. Supported: {string.Join(", ", AlertRules.Metrics)}.");
        if (!metric.Equals(dev.Metric, StringComparison.OrdinalIgnoreCase))
            return Reject(400, $"'{dev.Name}' reports '{dev.Metric}', not '{metric}'.");

        int signal = req.SignalStrength ?? -60;
        if (signal > 0 || signal < -120)
            return Reject(400, "Signal strength must be between -120 and 0 dBm.");

        double numeric;
        bool? state = null;
        ITelemetryPacket packet;

        if (metric == "state")
        {
            if (req.State is null) return Reject(400, "Actuator packets need a boolean 'state'.");
            state = req.State.Value;
            numeric = state.Value ? 1 : 0;
            packet = new TelemetryPacket<bool>(mac, "Actuator", dev.Location, state.Value, signal) { IsValidated = true };
        }
        else
        {
            if (req.Value is null) return Reject(400, "Packets need a numeric 'value'.");
            var problem = AlertRules.ValidateValue(metric, req.Value.Value);
            if (problem is not null) return Reject(400, problem);

            numeric = Math.Round(req.Value.Value, 2);
            packet = metric == "power"
                ? new TelemetryPacket<int>(mac, "PowerConsumption", dev.Location, (int)Math.Round(numeric), signal) { IsValidated = true }
                : new TelemetryPacket<float>(mac, "Environmental", dev.Location, (float)numeric, signal) { IsValidated = true };
        }

        var candidates = AlertRules.Evaluate(metric, numeric, signal, dev.Name);
        var critical = candidates.Where(c => c.Severity == AlertSeverity.Critical).ToList();

        var sp = new StreamPacket
        {
            Packet = packet,
            Mac = mac,
            DeviceName = dev.Name,
            Metric = metric,
            Unit = AlertRules.UnitFor(metric),
            Numeric = numeric,
            State = state,
            Signal = signal,
            Candidates = candidates,
            Lane = critical.Count > 0 ? Lane.Priority : Lane.Standard,
            Urgency = critical.Count > 0 ? critical.Min(c => c.Urgency) : 100
        };

        lock (_gate)
        {
            if (sp.Lane == Lane.Priority && _priority.Count >= MaxPriorityDepth)
                return Reject(429, "Priority queue is full.");
            if (sp.Lane == Lane.Standard && _standard.Count >= MaxStandardDepth)
            {
                _rejected++;
                return new IngestResult(false, 429, "Gateway is saturated — standard queue full. Back off and retry.", "Standard", 0, _standard.Count, _priority.Count);
            }

            sp.Sequence = ++_sequence;
            sp.EnqueuedAt = DateTime.UtcNow;
            if (sp.Lane == Lane.Priority) _priority.Enqueue(sp, (sp.Urgency, sp.Sequence));
            else _standard.Enqueue(sp);
            _ingested++;
            return new IngestResult(true, 202, null, sp.Lane.ToString(), sp.Sequence, _standard.Count, _priority.Count);
        }
    }

    private IngestResult Reject(int status, string error)
    {
        lock (_gate) _rejected++;
        return new IngestResult(false, status, error, "", 0, 0, 0);
    }

    // ═══ DRAIN (called by the background processor) ═════════════════
    /// <summary>All pending critical packets first (most urgent first), then up to <paramref name="maxStandard"/> standard ones.</summary>
    public List<StreamPacket> TakeBatch(int maxStandard)
    {
        var batch = new List<StreamPacket>();
        lock (_gate)
        {
            while (_priority.Count > 0) batch.Add(_priority.Dequeue());
            for (int i = 0; i < maxStandard && _standard.Count > 0; i++) batch.Add(_standard.Dequeue());
        }
        return batch;
    }

    public void Process(StreamPacket p)
    {
        var now = DateTime.UtcNow;
        var view = _registry.ApplyReading(p.Mac, p.Numeric, p.State, p.Signal, now);   // O(1) dictionary hit
        if (view is null) return;

        var raised = new List<AlertRecord>();
        bool reconnected = false;

        lock (_gate)
        {
            // ── a packet from a node we had marked disconnected means it is back ──
            var discKey = $"{p.Mac}|Disconnected";
            if (_activeErrorStates.Remove(discKey))
            {
                Resolve(discKey, now);
                reconnected = true;
            }

            // ── raise new alerts; the HashSet silently rejects duplicates ──
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in p.Candidates)
            {
                var key = $"{p.Mac}|{c.Type}";
                present.Add(c.Type);
                if (_activeErrorStates.Add(key))
                {
                    var rec = new AlertRecord
                    {
                        Key = key, Mac = p.Mac, DeviceName = view.Name, Type = c.Type,
                        Severity = c.Severity.ToString(), Message = c.Message, RaisedAt = now
                    };
                    _alertDetails[key] = rec;
                    AppendAlertLog(rec);
                    raised.Add(rec);
                }
                else
                {
                    _duplicatesSuppressed++;
                    if (_alertDetails.TryGetValue(key, out var existing))
                    {
                        existing.Occurrences++;
                        existing.Message = c.Message;
                    }
                }
            }

            // ── clear alerts whose condition is no longer true ──
            foreach (var type in AlertRules.TypesFor(p.Metric))
            {
                if (present.Contains(type)) continue;
                var key = $"{p.Mac}|{type}";
                if (_activeErrorStates.Remove(key)) Resolve(key, now);
            }

            // ── bookkeeping ──
            _processOrder++;
            // latency = time waiting while the processor was actually running
            var waitStart = new DateTime(Math.Max(p.EnqueuedAt.Ticks, Interlocked.Read(ref _resumedTicks)), DateTimeKind.Utc);
            var latency = Math.Max(0, (now - waitStart).TotalMilliseconds);
            if (p.Lane == Lane.Priority)
            {
                _processedPriority++;
                _latencyPriorityEma = _latencyPriorityEma == 0 ? latency : _latencyPriorityEma * 0.9 + latency * 0.1;
            }
            else
            {
                _processedStandard++;
                _latencyStandardEma = _latencyStandardEma == 0 ? latency : _latencyStandardEma * 0.9 + latency * 0.1;
            }

            _processedLog.Enqueue(new ProcessedRecord(p.Sequence, _processOrder, p.Lane.ToString(), view.Name, p.Metric, p.Display, Math.Round(latency, 1), now));
            while (_processedLog.Count > MaxLog) _processedLog.Dequeue();
        }

        // ── outside the lock: talk to other services ──
        foreach (var r in raised) _engine.RecordAlert(r.DeviceName, r.Type);
        ForwardToPart1(p, view, now, force: raised.Count > 0 || reconnected);
    }

    private void Resolve(string key, DateTime now)
    {
        if (_alertDetails.Remove(key, out var rec)) rec.ResolvedAt = now;
    }

    private void AppendAlertLog(AlertRecord rec)
    {
        _alertLog.Add(rec);
        if (_alertLog.Count > MaxAlertLog) _alertLog.RemoveAt(0);
    }

    // Part 1's store is not built for a continuous stream, so forward a throttled sample (plus every alert).
    private void ForwardToPart1(StreamPacket p, DeviceView view, DateTime now, bool force)
    {
        lock (_gate)
        {
            if (!force && _lastForward.TryGetValue(p.Mac, out var last) && now - last < _forwardEvery) return;
            _lastForward[p.Mac] = now;
        }

        switch (p.Packet)
        {
            case TelemetryPacket<float> f: _telemetry.StorePacket(f); break;
            case TelemetryPacket<int> i: _telemetry.StorePacket(i); break;
            case TelemetryPacket<bool> b: _telemetry.StorePacket(b); break;
        }
        _telemetry.AddSensorData(new SensorData(p.Mac, view.Category, view.Location, p.Numeric, p.Unit, p.Signal));
    }

    // ═══ DISCONNECT HANDLING ════════════════════════════════════════
    /// <summary>Called on a timer. Anything silent for longer than the threshold is flagged — once.</summary>
    public int CheckDisconnects(TimeSpan threshold)
    {
        var now = DateTime.UtcNow;
        var silent = _registry.SeenDevices().Where(d => now - d.LastSeen > threshold).ToList();
        int raisedCount = 0;
        var raised = new List<AlertRecord>();

        lock (_gate)
        {
            foreach (var d in silent)
            {
                var key = $"{d.Mac}|Disconnected";
                if (_activeErrorStates.Add(key))            // already flagged? Add() returns false and we do nothing
                {
                    var rec = new AlertRecord
                    {
                        Key = key, Mac = d.Mac, DeviceName = d.Name, Type = "Disconnected", Severity = "Critical",
                        Message = $"{d.Name} has been silent for {(int)(now - d.LastSeen).TotalSeconds}s.",
                        RaisedAt = now, Source = "watchdog"
                    };
                    _alertDetails[key] = rec;
                    AppendAlertLog(rec);
                    raised.Add(rec);
                    raisedCount++;
                }
            }
        }
        foreach (var r in raised) _engine.RecordAlert(r.DeviceName, r.Type);
        return raisedCount;
    }

    /// <summary>A neighbour or gateway reports that a node is unreachable. Many reports → still one alert.</summary>
    public (bool IsNew, string Name, long DuplicatesSuppressed) ReportDisconnect(string mac, string? reportedBy)
    {
        if (!_registry.TryGetView(mac, out var dev)) throw new KeyNotFoundException("Unknown device.");
        var key = $"{mac}|Disconnected";
        AlertRecord? raised = null;
        bool isNew;

        lock (_gate)
        {
            isNew = _activeErrorStates.Add(key);
            if (isNew)
            {
                raised = new AlertRecord
                {
                    Key = key, Mac = mac, DeviceName = dev.Name, Type = "Disconnected", Severity = "Critical",
                    Message = $"{dev.Name} reported unreachable by {(string.IsNullOrWhiteSpace(reportedBy) ? "a peer" : reportedBy)}.",
                    Source = "peer-report"
                };
                _alertDetails[key] = raised;
                AppendAlertLog(raised);
            }
            else
            {
                _duplicatesSuppressed++;
                if (_alertDetails.TryGetValue(key, out var existing)) existing.Occurrences++;
            }
        }
        if (raised is not null) _engine.RecordAlert(raised.DeviceName, raised.Type);
        lock (_gate) return (isNew, dev.Name, _duplicatesSuppressed);
    }

    // ═══ READ MODELS ════════════════════════════════════════════════
    public List<AlertRecord> GetActiveAlerts()
    {
        lock (_gate)
        {
            return _alertDetails.Values
                .OrderByDescending(a => a.Severity == "Critical").ThenByDescending(a => a.RaisedAt)
                .Select(a => a.Copy()).ToList();
        }
    }

    public object GetAlertSnapshot()
    {
        lock (_gate)
        {
            return new
            {
                active = _alertDetails.Values
                    .OrderByDescending(a => a.Severity == "Critical").ThenByDescending(a => a.RaisedAt)
                    .Select(a => a.Copy()).ToList(),
                activeKeys = _activeErrorStates.Count,
                duplicatesSuppressed = _duplicatesSuppressed,
                log = _alertLog.AsEnumerable().Reverse().Take(30).Select(a => a.Copy()).ToList()
            };
        }
    }

    public void SampleThroughput()
    {
        lock (_gate)
        {
            _throughputSamples.Enqueue((DateTime.UtcNow, _processedStandard + _processedPriority));
            while (_throughputSamples.Count > 12) _throughputSamples.Dequeue();
        }
    }

    public object GetQueueSnapshot()
    {
        var views = QueryDevices(null, null, null);

        lock (_gate)
        {
            double rate = 0;
            if (_throughputSamples.Count >= 2)
            {
                var first = _throughputSamples.Peek();
                var lastSample = _throughputSamples.Last();
                var secs = (lastSample.At - first.At).TotalSeconds;
                if (secs > 0) rate = (lastSample.Count - first.Count) / secs;
            }

            return new
            {
                paused = Paused,
                standardDepth = _standard.Count,
                priorityDepth = _priority.Count,
                standardCapacity = MaxStandardDepth,
                ingestedTotal = _ingested,
                rejectedTotal = _rejected,
                processedStandard = _processedStandard,
                processedPriority = _processedPriority,
                avgLatencyStandardMs = Math.Round(_latencyStandardEma, 1),
                avgLatencyPriorityMs = Math.Round(_latencyPriorityEma, 1),
                throughputPerSec = Math.Round(rate, 1),
                duplicatesSuppressed = _duplicatesSuppressed,
                devices = new
                {
                    total = views.Count,
                    healthy = views.Count(v => v.State == "Healthy"),
                    warning = views.Count(v => v.State == "Warning"),
                    critical = views.Count(v => v.State == "Critical"),
                    disconnected = views.Count(v => v.State == "Disconnected"),
                    awaiting = views.Count(v => v.State == "Awaiting")
                },
                pending = new
                {
                    priority = _priority.UnorderedItems.OrderBy(i => i.Priority).Take(8)
                        .Select(i => new PendingItem(i.Element.Sequence, i.Element.DeviceName, i.Element.Display, i.Element.Urgency)).ToList(),
                    standard = _standard.Take(8)
                        .Select(i => new PendingItem(i.Sequence, i.DeviceName, i.Display, i.Urgency)).ToList()
                },
                recent = _processedLog.Reverse().ToList()
            };
        }
    }

    // ═══ DEVICE QUERIES (search + filters) ══════════════════════════
    public List<DeviceView> QueryDevices(string? q, string? category, string? state)
    {
        _registry.SyncFrom(_telemetry);

        List<DeviceView> list;
        bool exact = false;
        if (!string.IsNullOrWhiteSpace(q) && _registry.TryGetView(q.Trim(), out var hit))
        {
            list = new List<DeviceView> { hit };        // O(1) fast path: the search text is a full MAC
            exact = true;
        }
        else list = _registry.Snapshot();

        Decorate(list);

        IEnumerable<DeviceView> result = list;
        if (!exact && !string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            result = result.Where(d =>
                d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                d.Mac.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                d.Location.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                d.Kind.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(category))
            result = result.Where(d => d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(state))
        {
            result = state.Equals("Alerting", StringComparison.OrdinalIgnoreCase)
                ? result.Where(d => d.State is "Critical" or "Warning" or "Disconnected")
                : result.Where(d => d.State.Equals(state, StringComparison.OrdinalIgnoreCase));
        }

        // Problem devices first, then alphabetical.
        return result.OrderBy(d => StateRank(d.State)).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DeviceView? GetDevice(string idOrMac)
    {
        var mac = ResolveMac(idOrMac);
        if (mac is null || !_registry.TryGetView(mac, out var v)) return null;
        Decorate(new List<DeviceView> { v });
        return v;
    }

    private static int StateRank(string state) => state switch
    {
        "Critical" => 0, "Disconnected" => 1, "Warning" => 2, "Awaiting" => 3, _ => 4
    };

    private void Decorate(List<DeviceView> views)
    {
        Dictionary<string, List<AlertRecord>> byMac;
        lock (_gate)
        {
            byMac = _alertDetails.Values
                .GroupBy(a => a.Mac, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(a => a.Copy()).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        foreach (var v in views)
        {
            if (byMac.TryGetValue(v.Mac, out var alerts))
            {
                v.Alerts = alerts.Select(a => new AlertView(a.Type, a.Severity, a.Message, a.RaisedAt)).ToList();
                v.State = alerts.Any(a => a.Type == "Disconnected") ? "Disconnected"
                        : alerts.Any(a => a.Severity == "Critical") ? "Critical" : "Warning";
            }
            else v.State = v.LastSeen is null ? "Awaiting" : "Healthy";
        }
    }
}
