using System;

namespace SmartX.Models;

/// <summary>
/// A single "Needs Attention" condition surfaced by the proactive alert
/// prioritisation strategy. Computed server-side by TelemetryService.GetNeedsAttention()
/// using O(1) dictionary lookups against registered sensors and their latest readings.
/// </summary>
public class AttentionItem
{
    public string SensorId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // "Stale" | "WeakSignal" | "CapacityBreach"
    public string Tag { get; set; } = string.Empty;

    // "warn" | "critical" — critical conditions (capacity/power-spike breaches)
    // are surfaced ahead of routine warnings.
    public string Severity { get; set; } = "warn";

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}