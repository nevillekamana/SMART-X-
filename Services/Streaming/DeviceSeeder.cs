using SmartX.Models;
using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

/// <summary>
/// Seeds a simulated Smart-X fleet (13 ESP32-style nodes) at start-up:
///  • registers each device through Part 1's TelemetryService, so it also appears in Part 1's Sensors table;
///  • back-fills ~20 minutes of history so timelines and trend lines are meaningful immediately;
///  • bootstraps the predictive engine with a short historical operator log.
/// Live readings still have to arrive through POST /api/stream/ingest — see the simulator.
/// </summary>
public class DeviceSeeder : IHostedService
{
    private readonly TelemetryService _telemetry;
    private readonly DeviceRegistry _registry;
    private readonly PredictiveEngine _engine;
    private readonly IConfiguration _config;

    public DeviceSeeder(TelemetryService telemetry, DeviceRegistry registry, PredictiveEngine engine, IConfiguration config)
    {
        _telemetry = telemetry;
        _registry = registry;
        _engine = engine;
        _config = config;
    }

    private record Seed(string Mac, string Name, string Kind, string Category, string Location, string Metric, double Base, double Noise, double? Capacity = null);

    private static readonly Seed[] Fleet =
    {
        new("A4:CF:12:00:00:01", "Moisture Sensor A", "Sensor", "Environmental", "Hydroponics-Bay-1", "moisture", 62, 1.2, 100),
        new("A4:CF:12:00:00:02", "Moisture Sensor B", "Sensor", "Environmental", "Hydroponics-Bay-2", "moisture", 58, 1.2, 100),
        new("A4:CF:12:00:00:03", "Greenhouse Temp 1",  "Sensor", "Environmental", "Greenhouse-North", "temperature", 24, 0.4),
        new("A4:CF:12:00:00:04", "Greenhouse Temp 2",  "Sensor", "Environmental", "Greenhouse-South", "temperature", 26, 0.4),
        new("A4:CF:12:00:00:05", "Smart Meter 1", "Meter", "PowerConsumption", "Facility-A", "power", 1800, 90),
        new("A4:CF:12:00:00:06", "Smart Meter 2", "Meter", "PowerConsumption", "Zone-2", "power", 2300, 110),
        new("A4:CF:12:00:00:07", "Smart Meter 3", "Meter", "PowerConsumption", "Substation-1", "power", 1400, 70),
        new("A4:CF:12:00:00:08", "Pump A",  "Pump",  "Actuator", "Hydroponics-Bay-2", "state", 0, 0),
        new("A4:CF:12:00:00:09", "Pump B",  "Pump",  "Actuator", "Hydroponics-Bay-1", "state", 0, 0),
        new("A4:CF:12:00:00:0A", "Relay 3", "Relay", "Actuator", "Zone-2", "state", 0, 0),
        new("A4:CF:12:00:00:0B", "Valve 1", "Valve", "Actuator", "Hydroponics-Bay-1", "state", 0, 0),
        new("A4:CF:12:00:00:0C", "Valve 2", "Valve", "Actuator", "Hydroponics-Bay-2", "state", 0, 0),
        new("A4:CF:12:00:00:0D", "Fan 1",   "Fan",   "Actuator", "Greenhouse-North", "state", 0, 0),
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.GetValue("Streaming:SeedDemoDevices", true)) return Task.CompletedTask;

        var rand = new Random(7);
        var now = DateTime.UtcNow;

        foreach (var s in Fleet)
        {
            var sensor = _telemetry.GetSensorByMac(s.Mac);
            if (sensor is null)
            {
                try
                {
                    sensor = _telemetry.RegisterSensor(new SensorRegistration
                    {
                        MacAddress = s.Mac, Location = s.Location, Category = s.Category, CapacityThreshold = s.Capacity
                    });
                }
                catch (InvalidOperationException) { sensor = _telemetry.GetSensorByMac(s.Mac); }
            }
            if (sensor is null) continue;

            _registry.EnsureFromSensor(sensor, s.Name, s.Kind, s.Metric);

            // 20 minutes of history, one point every 20 s, gentle mean-reverting noise
            if (s.Category != "Actuator" && _registry.HistoryCount(s.Mac) == 0)
            {
                var points = new List<ReadingPoint>();
                double v = s.Base;
                for (int i = 60; i >= 1; i--)
                {
                    v += (s.Base - v) * 0.3 + (rand.NextDouble() - 0.5) * 2 * s.Noise;
                    points.Add(new ReadingPoint(now.AddSeconds(-i * 20), Math.Round(v, 2), -55 - rand.Next(0, 15)));
                }
                _registry.SeedHistory(s.Mac, points);
            }
        }

        TrainEngine(now);
        return Task.CompletedTask;
    }

    /// <summary>A short, clearly-seeded operator log so the engine has something to learn from on first run.</summary>
    private void TrainEngine(DateTime now)
    {
        var events = new List<OperatorEvent>();

        void Session(DateTime t, string device, string alert, string searched, string cmdDevice, string cmd)
        {
            events.Add(new OperatorEvent(t, EventKind.Alert, device, alert));
            if (searched != "") events.Add(new OperatorEvent(t.AddSeconds(9), EventKind.Search, searched, ""));
            events.Add(new OperatorEvent(t.AddSeconds(17), EventKind.Command, cmdDevice, cmd));
        }

        // Moisture Sensor B dries out → operator checks Pump A → switches it on (6 past incidents)
        for (int i = 0; i < 6; i++)
            Session(now.AddHours(-(i * 4 + 2)), "Moisture Sensor B", "MoistureLow", "Pump A", "Pump A", "On");
        // Smart Meter 2 spikes → operator checks Relay 3 → switches it off (4 past incidents)
        for (int i = 0; i < 4; i++)
            Session(now.AddHours(-(i * 5 + 3)).AddMinutes(-20), "Smart Meter 2", "PowerSpike", "Relay 3", "Relay 3", "Off");
        // Greenhouse Temp 2 overheats → operator starts Fan 1 (3 past incidents)
        for (int i = 0; i < 3; i++)
            Session(now.AddHours(-(i * 7 + 5)).AddMinutes(-40), "Greenhouse Temp 2", "OverTemp", "", "Fan 1", "On");

        _engine.Train(events);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
