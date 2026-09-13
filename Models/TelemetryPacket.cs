using System;

namespace SmartX.Models;

/// <summary>
/// Common interface for all telemetry packets, enabling generic storage and retrieval.
/// </summary>
public interface ITelemetryPacket
{
    string SensorId { get; }
    string SensorCategory { get; }
    string Location { get; }
    DateTime Timestamp { get; }
    int SignalStrength { get; }
    bool IsValidated { get; }
}

/// <summary>
/// Generic wrapper class that handles disparate incoming data structures uniformly
/// without expensive boxing/unboxing operations.
/// T can be float (temperature), int (power metrics), bool (smart switch triggers), etc.
/// </summary>
public class TelemetryPacket<T> : ITelemetryPacket where T : struct
{
    public string SensorId { get; set; } = string.Empty;
    public string SensorCategory { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // The generic data payload – no boxing/unboxing since T is a value type constrained by 'struct'
    public T Value { get; set; }

    // Quality-of-service metadata
    public int SignalStrength { get; set; }
    public bool IsValidated { get; set; }

    public TelemetryPacket() { }

    public TelemetryPacket(string sensorId, string category, string location, T value, int signal = -60)
    {
        SensorId = sensorId;
        SensorCategory = category;
        Location = location;
        Value = value;
        SignalStrength = signal;
        IsValidated = false;
    }

    public override string ToString()
        => $"[{Timestamp:HH:mm:ss.fff}] {SensorId} ({SensorCategory}) @ {Location} => {Value} [Signal: {SignalStrength}dBm]";
}
