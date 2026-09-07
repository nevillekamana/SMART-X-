using System;

namespace SmartX.Models;

/// <summary>
/// Represents a sensor reading with operator overloading for direct aggregation
/// and delta comparisons of sensor values in code.
/// e.g. Meter3 = Meter1 + Meter2;  or  diff = Meter1 - Meter2;
/// </summary>
public class SensorData : IComparable<SensorData>
{
    public string SensorId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public SensorData() { }

    public SensorData(string id, string category, string location, double value, string unit)
    {
        SensorId = id;
        Category = category;
        Location = location;
        Value = value;
        Unit = unit;
    }

    // ─── Operator Overloading: + (Aggregation) ───
    // Allows: Meter3 = Meter1 + Meter2
    public static SensorData operator +(SensorData a, SensorData b)
    {
        if (a.Unit != b.Unit)
            throw new InvalidOperationException($"Cannot aggregate sensors with different units ({a.Unit} vs {b.Unit}).");
        return new SensorData
        {
            SensorId = $"{a.SensorId}+{b.SensorId}",
            Category = a.Category,
            Location = $"{a.Location}|{b.Location}",
            Value = a.Value + b.Value,
            Unit = a.Unit,
            Timestamp = a.Timestamp > b.Timestamp ? a.Timestamp : b.Timestamp
        };
    }

    // ─── Operator Overloading: - (Delta Comparison) ───
    // Allows: diff = Meter1 - Meter2
    public static SensorData operator -(SensorData a, SensorData b)
    {
        if (a.Unit != b.Unit)
            throw new InvalidOperationException($"Cannot compute delta of sensors with different units ({a.Unit} vs {b.Unit}).");
        return new SensorData
        {
            SensorId = $"{a.SensorId}-{b.SensorId}",
            Category = a.Category,
            Location = $"{a.Location}|{b.Location}",
            Value = a.Value - b.Value,
            Unit = a.Unit,
            Timestamp = a.Timestamp > b.Timestamp ? a.Timestamp : b.Timestamp
        };
    }

    // ─── Operator Overloading: > (Threshold breach check) ───
    public static bool operator >(SensorData a, SensorData b) => a.Value > b.Value;

    // ─── Operator Overloading: < ───
    public static bool operator <(SensorData a, SensorData b) => a.Value < b.Value;

    // ─── Operator Overloading: >= ───
    public static bool operator >=(SensorData a, SensorData b) => a.Value >= b.Value;

    // ─── Operator Overloading: <= ───
    public static bool operator <=(SensorData a, SensorData b) => a.Value <= b.Value;

    // IComparable implementation for sorting / ranking
    public int CompareTo(SensorData? other)
        => other is null ? 1 : Value.CompareTo(other.Value);

    public override string ToString()
        => $"{SensorId}: {Value:F2}{Unit} ({Category}) @ {Location}";
}
