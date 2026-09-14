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

    // HashSet for O(1) duplicate detection during consolidation
    private readonly HashSet<double> _consolidatedValuesSeen = new();

    // Pending batch buffer for meaningful jagged-array batching
    private readonly List<double> _pendingBatchBuffer = new();

    // Generic telemetry packets stored by sensor ID using ITelemetryPacket interface
    private readonly Dictionary<string, ITelemetryPacket> _packets = new();

    // Sensor registrations
    private readonly List<SensorRegistration> _sensors = new();
    private readonly Dictionary<string, SensorData> _latestData = new();

    // O(1) sensor lookup by either MAC address or generated Id, so GetSensor /
    // GetSensorByMac / ValidateTelemetryType / GetNeedsAttention don't have to
    // scan the full sensor list on every telemetry push.
    private readonly Dictionary<string, SensorRegistration> _sensorsByKey = new(StringComparer.OrdinalIgnoreCase);

    // Default capacity/power-spike thresholds applied at registration time when
    // a sensor doesn't specify its own CapacityThreshold. Only categories with a
    // meaningful upper bound get a default; Actuator states are excluded.
    private static readonly Dictionary<string, double> DefaultCapacityThresholds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "PowerConsumption", 4000 }, // W — smart-meter power-spike threshold
        { "Environmental", 45 }       // °C — over-temperature threshold
    };

    private const double WeakSignalThresholdDbm = -75;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(15);

    // Operator-overloaded sensor data store
    private readonly List<SensorData> _sensorDataStore = new();

    // ─── Advanced Arrays: Add a batch using jagged arrays ───
    /// <summary>
    /// Helper to add a value to both consolidated history and the deduplication set.
    /// Centralizing this prevents drift between the two collections.
    /// </summary>
    private void AddToConsolidatedHistory(double value)
    {
        _consolidatedHistory.Add(value);
        _consolidatedValuesSeen.Add(value);
    }

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

        // Transfer batch data into optimised List<T> and dedup set
        foreach (var val in readings)
        {
            AddToConsolidatedHistory(val);
        }
    }

    // ─── Transfer all raw batches into List<T> with deduplication ───
    public List<double> ConsolidateAllBatches()
    {
        for (int i = 0; i < _batchIndex; i++)
        {
            if (_rawBatches[i] != null)
            {
                foreach (var val in _rawBatches[i])
                {
                    // Use HashSet.Add for O(1) duplicate detection instead of Contains O(n)
                    if (_consolidatedValuesSeen.Add(val))
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

        if (_sensorsByKey.ContainsKey(reg.MacAddress))
            throw new InvalidOperationException($"A sensor with MAC address '{reg.MacAddress}' is already registered.");

        reg.Id = Guid.NewGuid().ToString();
        reg.RegisteredAt = DateTime.UtcNow;

        // Assign a category-default capacity threshold if the caller didn't set one,
        // so the Needs Attention panel has something to compare readings against.
        if (!reg.CapacityThreshold.HasValue && DefaultCapacityThresholds.TryGetValue(reg.Category, out var defaultThreshold))
            reg.CapacityThreshold = defaultThreshold;

        _sensors.Add(reg);

        // Index by both MAC and generated Id for O(1) lookups elsewhere.
        _sensorsByKey[reg.MacAddress] = reg;
        _sensorsByKey[reg.Id] = reg;

        return reg;
    }

    public List<SensorRegistration> GetAllSensors() => _sensors.ToList();

    // O(1) — Dictionary<string, SensorRegistration> lookup instead of a linear scan.
    public SensorRegistration? GetSensor(string id) => _sensorsByKey.TryGetValue(id, out var s) ? s : null;

    // O(1) — same backing dictionary, since MAC and Id are both indexed into it.
    public SensorRegistration? GetSensorByMac(string mac) => _sensorsByKey.TryGetValue(mac, out var s) ? s : null;

    // ─── Validate Telemetry Type Against Registered Sensor ───
    /// <summary>
    /// Validates that telemetry being pushed matches a registered sensor's category.
    /// Throws ArgumentException if SensorId is missing or type isn't recognised.
    /// Throws KeyNotFoundException if no matching sensor exists.
    /// Throws InvalidOperationException if sensor category doesn't match telemetry type.
    /// Returns the matched SensorRegistration on success.
    /// </summary>
    public SensorRegistration ValidateTelemetryType(string sensorId, string type)
    {
        if (string.IsNullOrWhiteSpace(sensorId))
            throw new ArgumentException("SensorId is required and cannot be empty.");

        // Map telemetry type to expected category
        var typeToCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "temperature", "Environmental" },
            { "humidity", "Environmental" },
            { "environmental", "Environmental" },
            { "power", "PowerConsumption" },
            { "powerconsumption", "PowerConsumption" },
            { "actuator", "Actuator" },
            { "switch", "Actuator" }
        };

        if (!typeToCategory.TryGetValue(type, out var expectedCategory))
            throw new ArgumentException($"Unknown sensor type: {type}. Supported: environmental, power, actuator");

        // Look up sensor by ID or MAC address
        var sensor = GetSensor(sensorId);
        if (sensor is null)
            sensor = GetSensorByMac(sensorId);

        if (sensor is null)
            throw new KeyNotFoundException($"No registered sensor found with ID or MAC '{sensorId}'.");

        // Validate category matches
        if (!string.Equals(sensor.Category, expectedCategory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sensor '{sensorId}' is registered as '{sensor.Category}' but telemetry type '{type}' requires '{expectedCategory}'.");

        return sensor;
    }

    // ─── Validate Sensor IDs Are Provided ───
    /// <summary>
    /// Helper to ensure both sensor IDs are provided for operations like aggregate, delta, compare.
    /// Throws ArgumentException if either is null or whitespace.
    /// </summary>
    private void ValidateSensorIdsProvided(string id1, string id2)
    {
        if (string.IsNullOrWhiteSpace(id1))
            throw new ArgumentException("First sensor ID is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(id2))
            throw new ArgumentException("Second sensor ID is required and cannot be empty.");
    }
    public void AddSensorData(SensorData data)
    {
        _sensorDataStore.Add(data);
        _latestData[data.SensorId] = data;

        // Immediately add to consolidated history (real-time view is never delayed)
        AddToConsolidatedHistory(data.Value);

        // Add to pending batch buffer
        _pendingBatchBuffer.Add(data.Value);

        // Commit buffer as a row when it reaches BatchSize
        if (_pendingBatchBuffer.Count >= BatchSize)
        {
            AddRawBatch(_pendingBatchBuffer.ToArray());
            _pendingBatchBuffer.Clear();
        }
    }

    // ─── Aggregate two sensors using operator overloading ───
    public SensorData AggregateSensors(string id1, string id2)
    {
        ValidateSensorIdsProvided(id1, id2);
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
        ValidateSensorIdsProvided(id1, id2);
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
        ValidateSensorIdsProvided(id1, id2);
        if (!_latestData.TryGetValue(id1, out var a))
            throw new KeyNotFoundException($"Sensor {id1} not found.");
        if (!_latestData.TryGetValue(id2, out var b))
            throw new KeyNotFoundException($"Sensor {id2} not found.");

        // Validate units match before comparison
        if (!string.Equals(a.Unit, b.Unit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot compare sensors with different units ({a.Unit} vs {b.Unit}).");

        if (a > b) return $"{a.SensorId} ({a.Value}) > {b.SensorId} ({b.Value})";
        if (a < b) return $"{a.SensorId} ({a.Value}) < {b.SensorId} ({b.Value})";
        return $"{a.SensorId} ({a.Value}) == {b.SensorId} ({b.Value})";
    }

    // ─── Server-side Needs Attention computation (proactive alert prioritisation) ───
    // Runs entirely against O(1) dictionary lookups (_latestData, _sensorsByKey) —
    // no linear scan of telemetry history is required per sensor.
    public List<AttentionItem> GetNeedsAttention()
    {
        var items = new List<AttentionItem>();
        var now = DateTime.UtcNow;

        foreach (var sensor in _sensors)
        {
            var key = string.IsNullOrEmpty(sensor.MacAddress) ? sensor.Id : sensor.MacAddress;

            // O(1) dictionary lookup for this sensor's latest reading.
            if (!_latestData.TryGetValue(key, out var last))
                continue; // never reported — nothing to evaluate yet

            var age = now - last.Timestamp;

            if (age > StaleThreshold)
            {
                items.Add(new AttentionItem
                {
                    SensorId = key,
                    Title = $"{key} has gone quiet",
                    Description = $"No reading for {(int)age.TotalMinutes} minutes · {sensor.Location}",
                    Tag = "Stale",
                    Severity = "warn"
                });
            }

            if (last.SignalStrength.HasValue && last.SignalStrength.Value < WeakSignalThresholdDbm)
            {
                items.Add(new AttentionItem
                {
                    SensorId = key,
                    Title = "Weak signal detected",
                    Description = $"{key} at {last.SignalStrength} dBm",
                    Tag = "WeakSignal",
                    Severity = "warn"
                });
            }

            if (sensor.CapacityThreshold.HasValue && last.Value > sensor.CapacityThreshold.Value)
            {
                var isPower = string.Equals(sensor.Category, "PowerConsumption", StringComparison.OrdinalIgnoreCase);
                items.Add(new AttentionItem
                {
                    SensorId = key,
                    Title = isPower ? "Power spike detected" : "Capacity breach",
                    Description = $"{key} reading {last.Value}{last.Unit} exceeds configured limit of {sensor.CapacityThreshold}{last.Unit}",
                    Tag = "CapacityBreach",
                    Severity = "critical"
                });
            }
        }

        // Critical conditions (capacity/power-spike breaches) surface ahead of routine warnings.
        return items.OrderBy(i => i.Severity == "critical" ? 0 : 1).ToList();
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