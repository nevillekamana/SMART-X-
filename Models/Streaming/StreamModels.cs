using System.Text.Json.Serialization;

namespace SmartX.Models.Streaming;

// ═══════════════════════════════════════════════════════════════════
//  PART 2 — Real-Time Command Stream: shared models
// ═══════════════════════════════════════════════════════════════════

/// <summary>Which ingestion lane a packet travels in.</summary>
public enum Lane { Standard, Priority }

public enum AlertSeverity { Warning = 1, Critical = 2 }

// ─── Request DTOs ───────────────────────────────────────────────────
/// <summary>What an ESP32 would POST: who it is, what it measured, and how strong its signal is.</summary>
public record IngestRequest(string? DeviceId, string? Metric, double? Value, bool? State, int? SignalStrength);
public record CommandRequest(string? DeviceId, string? Action, string? Operator);
public record BulkCommandRequest(string? Kind, string? Action, string? Operator);
public record SearchEventRequest(string? Query);
public record DisconnectReportRequest(string? DeviceId, string? ReportedBy);

// ─── Readings ───────────────────────────────────────────────────────
public record ReadingPoint(DateTime Timestamp, double Value, int Signal);

// ─── Alerts ─────────────────────────────────────────────────────────
/// <summary>A condition detected on a single reading (pure function of the reading, so it can be evaluated at ingest time).</summary>
public record AlertCandidate(string Type, AlertSeverity Severity, string Message, int Urgency);

public class AlertRecord
{
    public string Key { get; set; } = string.Empty;          // "MAC|Type" — the value stored in the HashSet
    public string Mac { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning";
    public string Message { get; set; } = string.Empty;
    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public int Occurrences { get; set; } = 1;                // repeat readings absorbed by the HashSet
    public string Source { get; set; } = "telemetry";

    public AlertRecord Copy() => (AlertRecord)MemberwiseClone();
}

public record AlertView(string Type, string Severity, string Message, DateTime RaisedAt);

// ─── Device view (what the dashboard receives) ──────────────────────
public class DeviceView
{
    public string Mac { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;         // Sensor / Meter / Pump / Valve / Relay / Fan
    public string Category { get; set; } = string.Empty;     // Environmental / PowerConsumption / Actuator
    public string Location { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;       // temperature / moisture / power / state
    public string Unit { get; set; } = string.Empty;
    public double? LastValue { get; set; }
    public int? Signal { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool IsOn { get; set; }                           // commanded state (actuators)
    public bool? ReportedOn { get; set; }                    // last state the device itself reported
    public bool InSync => ReportedOn is null || ReportedOn == IsOn;
    public string State { get; set; } = "Awaiting";          // Healthy / Warning / Critical / Disconnected / Awaiting
    public List<AlertView> Alerts { get; set; } = new();
    public double[] Spark { get; set; } = Array.Empty<double>();
    public long PacketCount { get; set; }
    public double? CriticalLow { get; set; }
    public double? CriticalHigh { get; set; }

    [JsonIgnore] public List<ReadingPoint> RecentPoints { get; set; } = new();
}

// ─── Queued packet ──────────────────────────────────────────────────
public class StreamPacket
{
    public long Sequence { get; set; }
    public ITelemetryPacket Packet { get; set; } = default!;  // TelemetryPacket<float|int|bool> — no boxing, it is a class
    public string Mac { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double Numeric { get; set; }
    public bool? State { get; set; }
    public int Signal { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public Lane Lane { get; set; } = Lane.Standard;
    public int Urgency { get; set; } = 100;                   // lower = more urgent (priority-queue rank)
    public List<AlertCandidate> Candidates { get; set; } = new();

    public string Display => Metric == "state" ? (State == true ? "ON" : "OFF") : $"{Numeric:0.##}{Unit}";
}

public record IngestResult(bool Accepted, int StatusCode, string? Error, string Lane, long Sequence, int StandardDepth, int PriorityDepth);

public record ProcessedRecord(long Sequence, long Order, string Lane, string DeviceName, string Metric, string Display, double LatencyMs, DateTime ProcessedAt);
public record PendingItem(long Sequence, string DeviceName, string Display, int Urgency);

// ─── Commands ───────────────────────────────────────────────────────
public record CommandTarget(string Mac, string Name, bool Previous, bool Applied);

public class CommandRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "Command";            // Command / BulkCommand / Undo
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Operator { get; set; } = "operator";
    public List<CommandTarget> Targets { get; set; } = new();
    public bool Undone { get; set; }
    public long? UndoOf { get; set; }
}

// ─── Predictive engine output ───────────────────────────────────────
public class SuggestedAction
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "RunCommand";         // RunCommand / Inspect
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int Evidence { get; set; }
    public string DeviceMac { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public class Insight
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;         // PredictedBreach / Anomaly / AtRisk
    public string Severity { get; set; } = "info";           // info / warn / critical
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string DeviceMac { get; set; } = string.Empty;
    public double Confidence { get; set; }
}

public record DeviceRisk(string Mac, string Name, double Score, string Why);
public record FrequentSearch(string Name, int Count);

public class InsightsResult
{
    public List<SuggestedAction> SuggestedActions { get; set; } = new();
    public List<Insight> Insights { get; set; } = new();
    public List<DeviceRisk> AtRisk { get; set; } = new();
    public List<FrequentSearch> FrequentSearches { get; set; } = new();
    public object Model { get; set; } = new { };
}
