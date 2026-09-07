using System;
using System.Collections.Generic;
using System.Linq;
using SmartX.Models;

namespace SmartX.Services;

/// <summary>
/// Core telemetry service demonstrating:
///  - Advanced jagged arrays for raw batch storage before List&lt;T&gt; transfer
///  - Generic TelemetryPacket&lt;T&gt; storage without boxing/unboxing
///  - Operator-overloaded SensorData aggregation
/// </summary>
public class TelemetryService
{
    // ─── Jagged array storage for raw historical telemetry batches ───
    // Each inner double[] is a batch of readings; outer array holds sequential batches
    private double[][] _rawBatches = Array.Empty<double[]>();
    private int _batchIndex = 0;
    private const int MaxBatches = 20;
    private const int BatchSize = 10;

    // Optimised List<T> after transferring from jagged array
    private readonly List<double> _consolidatedHistory = new();

    // Generic telemetry packets stored by sensor ID
    private readonly Dictionary<string, object> _packets = new();

    // Sensor registrations
    private readonly List<SensorRegistration> _sensors = new();
    private readonly Dictionary<string, SensorData> _latestData = new();

    // Operator-overloaded sensor data store
    private readonly List<SensorData> _sensorDataStore = new();

    // ─── Advanced Arrays: Add a batch using jagged arrays ───
    public void AddRawBatch(double[] readings)
    {
        // Expand jagged array if needed
        if (_rawBatches.Length == 0 || _batchIndex >= _rawBatches.Length)
        {
            int newSize = _rawBatches.Length == 0 ? MaxBatches : _rawBatches.Length * 2;
            var newBatches = new double[newSize][];
            Array.Copy(_rawBatches, newBatches, _rawBatches.Length);
            _rawBatches = newBatches;
        }

        _rawBatches[_batchIndex] = readings;
        _batchIndex++;

        // Transfer batch data into optimised List<T>
        foreach (var val in readings)
        {
            _consolidatedHistory.Add(val);
        }
    }

    // ─── Transfer all raw batches into List<T> ───
    public List<double> ConsolidateAllBatches()
    {
        for (int i = 0; i < _batchIndex; i++)
        {
            if (_rawBatches[i] != null)
            {
                foreach (var val in _rawBatches[i])
                {
                    if (!_consolidatedHistory.Contains(val))
                        _consolidatedHistory.Add(val);
                }
            }
        }
        return new List<double>(_consolidatedHistory);
    }

    // ─── Store a generic TelemetryPacket<T> ───
    public void StorePacket<T>(TelemetryPacket<T> packet) where T : struct
    {
        _packets[packet.SensorId] = packet;
    }

    // ─── Retrieve a generic TelemetryPacket<T> without boxing/unboxing ───
    public TelemetryPacket<T>? GetPacket<T>(string sensorId) where T : struct
    {
        if (_packets.TryGetValue(sensorId, out var obj) && obj is TelemetryPacket<T> packet)
            return packet;
        return null;
    }

    // ─── Sensor Registration ───
    // Validates required fields and rejects duplicate MAC addresses before registering.
    public SensorRegistration RegisterSensor(SensorRegistration reg)
    {
        if (string.IsNullOrWhiteSpace(reg.MacAddress))
            throw new ArgumentException("MAC address / unique identifier is required.");

        if (string.IsNullOrWhiteSpace(reg.Location))
            throw new ArgumentException("Sensor deployment location is required.");

        if (string.IsNullOrWhiteSpace(reg.Category))
            throw new ArgumentException("Sensor category is required.");

        var validCategories = new[] { "Environmental", "PowerConsumption", "Actuator" };
        if (!validCategories.Contains(reg.Category, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Category must be one of: {string.Join(", ", validCategories)}.");

        if (_sensors.Any(s => string.Equals(s.MacAddress, reg.MacAddress, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A sensor with MAC address '{reg.MacAddress}' is already registered.");

        reg.Id = Guid.NewGuid().ToString();
        reg.RegisteredAt = DateTime.UtcNow;
        _sensors.Add(reg);
        return reg;
    }

    public List<SensorRegistration> GetAllSensors() => _sensors.ToList();

    public SensorRegistration? GetSensor(string id) => _sensors.FirstOrDefault(s => s.Id == id);

    public SensorRegistration? GetSensorByMac(string mac) => _sensors.FirstOrDefault(s => s.MacAddress == mac);

    // ─── Add SensorData with operator overloading support ───
    public void AddSensorData(SensorData data)
    {
        _sensorDataStore.Add(data);
        _latestData[data.SensorId] = data;

        // Add to raw batch (jagged array) then to list
        double[] batch = new[] { data.Value };
        AddRawBatch(batch);
    }

    // ─── Aggregate two sensors using operator overloading ───
    public SensorData AggregateSensors(string id1, string id2)
    {
        if (!_latestData.TryGetValue(id1, out var a))
            throw new KeyNotFoundException($"Sensor {id1} not found.");
        if (!_latestData.TryGetValue(id2, out var b))
            throw new KeyNotFoundException($"Sensor {id2} not found.");

        // Using the overloaded + operator: Meter3 = Meter1 + Meter2
        return a + b;
    }

    // ─── Delta comparison using operator overloading ───
    public SensorData DeltaSensors(string id1, string id2)
    {
        if (!_latestData.TryGetValue(id1, out var a))
            throw new KeyNotFoundException($"Sensor {id1} not found.");
        if (!_latestData.TryGetValue(id2, out var b))
            throw new KeyNotFoundException($"Sensor {id2} not found.");

        // Using the overloaded - operator
        return a - b;
    }

    // ─── Compare sensors using overloaded comparison operators ───
    public string CompareSensors(string id1, string id2)
    {
        if (!_latestData.TryGetValue(id1, out var a))
            throw new KeyNotFoundException($"Sensor {id1} not found.");
        if (!_latestData.TryGetValue(id2, out var b))
            throw new KeyNotFoundException($"Sensor {id2} not found.");

        if (a > b) return $"{a.SensorId} ({a.Value}) > {b.SensorId} ({b.Value})";
        if (a < b) return $"{a.SensorId} ({a.Value}) < {b.SensorId} ({b.Value})";
        return $"{a.SensorId} ({a.Value}) == {b.SensorId} ({b.Value})";
    }

    // ─── Get telemetry history (from consolidated List<T>) ───
    public List<double> GetHistory() => new(_consolidatedHistory);

    // ─── Get all sensor data ───
    public List<SensorData> GetAllSensorData() => _sensorDataStore.ToList();

    // ─── Get raw batch info ───
    public (int BatchCount, int TotalReadings, double[][] RawBatches) GetBatchInfo()
        => (_batchIndex, _consolidatedHistory.Count, _rawBatches);

    // ─── Simulate telemetry generation ───
    public void SimulateTelemetry()
    {
        var rand = new Random();
        var categories = new[] { "Environmental", "PowerConsumption", "Actuator" };
        var locations = new[] { "Room-101", "Zone-1", "Node-A3", "SubZone-B2", "Facility-A" };

        for (int i = 0; i < 5; i++)
        {
            string id = $"SIM-{rand.Next(1000, 9999)}";
            string cat = categories[rand.Next(categories.Length)];
            string loc = locations[rand.Next(locations.Length)];
            double val = cat switch
            {
                "Environmental" => Math.Round(rand.NextDouble() * 40 + 10, 2),  // 10-50°C
                "PowerConsumption" => Math.Round(rand.NextDouble() * 5000, 2),       // 0-5000W
                "Actuator" => rand.Next(0, 2),                                      // 0 or 1
                _ => 0
            };
            string unit = cat switch
            {
                "Environmental" => "°C",
                "PowerConsumption" => "W",
                "Actuator" => "state",
                _ => ""
            };

            AddSensorData(new SensorData(id, cat, loc, val, unit));
        }
    }
}