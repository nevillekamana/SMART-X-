using System;
using System.Collections.Generic;

namespace SmartX.Models;

/// <summary>
/// Sensor registration record with MAC address, location, category, and media/log attachments.
/// </summary>
public class SensorRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MacAddress { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    // Operating limit for this sensor's readings (e.g. max safe wattage
    // for a PowerConsumption sensor). Null means no capacity check applies.
    // If left unset at registration time, RegisterSensor() assigns a sensible
    // category default so the Needs Attention capacity/power-spike check has
    // something to compare against.
    public double? CapacityThreshold { get; set; }

    // Attached files (config files, deployment photos, hardware logs)
    public List<SensorAttachment> Attachments { get; set; } = new();

    // Telemetry history (using advanced jagged arrays before List transfer)
    // Jagged array: each batch is a double[]; the outer array holds sequential historical batches
    public double[][]? TelemetryBatchesRaw { get; set; }
    public List<double> TelemetryHistory { get; set; } = new();
}

public class SensorAttachment
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime AttachedAt { get; set; } = DateTime.UtcNow;
}
