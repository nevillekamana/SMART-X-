using SmartX.Models;
using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

/// <summary>
/// Seeds a simulated Smart-X fleet (13 ESP32-style nodes) at start-up:
///  • registers each device through Part 1's TelemetryService, so it also appears in Part 1's Sensors table;
///  • back-fills Mesh:SeedHistoryHours (default 24 h) of history FIRST, oldest first — sensors/meters every 30 s with
///    diurnal / sawtooth patterns, actuators one state report every 5 min — into the AVL history archive;
///  • then ~20 minutes of live-seed history (every 20 s) so timelines and trend lines are meaningful immediately;
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
        var seedHours = Math.Clamp(_config.GetValue("Mesh:SeedHistoryHours", 24), 0, 24 * 14);

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

            bool fresh = _registry.HistoryCount(s.Mac) == 0;

            // 1) Long back-fill first (oldest → newest) so the live seed below stays in key order.
            if (seedHours > 0 && fresh)
                _registry.SeedHistory(s.Mac, BackfillHistory(s, now, seedHours, rand), updateLive: false);

            // 2) 20 minutes of history, one point every 20 s, gentle mean-reverting noise
            if (s.Category != "Actuator" && fresh)
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

    /// <summary>
    /// Generates the long history window ending 20 minutes before "now" (where the live seed begins), oldest first.
    /// Sensors and meters report every 30 s with a believable shape; actuators report their state every 5 minutes.
    /// </summary>
    private static IEnumerable<ReadingPoint> BackfillHistory(Seed s, DateTime now, int hours, Random rand)
    {
        var end = now.AddMinutes(-20).AddSeconds(-1);
        var start = now.AddHours(-hours);
        bool actuator = s.Category == "Actuator";
        var step = actuator ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);

        for (var t = start; t < end; t += step)
        {
            double hour = t.Hour + t.Minute / 60.0;
            int signal = -55 - rand.Next(0, 15);
            double noise = (rand.NextDouble() - 0.5) * 2 * s.Noise;
            double v;

            if (actuator) v = ActuatorState(s.Name, t) ? 1 : 0;
            else if (s.Metric == "moisture")
            {
                // sawtooth: the root zone dries ~14 % over a 4-hour cycle, then the pump irrigates and it jumps back up
                double phase = (t - DateTime.UnixEpoch).TotalHours % 4 / 4;
                v = s.Base + 6 - 14 * phase + noise;
            }
            else if (s.Metric == "temperature")
                v = s.Base + 4 * Math.Sin(2 * Math.PI * (hour - 9) / 24) + noise;        // diurnal curve peaking mid-afternoon
            else
                v = s.Base * (0.85 + 0.3 * Math.Sin(2 * Math.PI * (hour - 9) / 24)) + noise;   // daytime load

            yield return new ReadingPoint(t, Math.Round(v, 2), signal);
        }
    }

    /// <summary>Simple duty cycles so the actuator step-charts show real on/off transitions.</summary>
    private static bool ActuatorState(string name, DateTime t) => name switch
    {
        "Pump A" => t.Hour % 4 == 0 && t.Minute < 20,
        "Pump B" => t.Hour % 6 == 1 && t.Minute < 15,
        "Relay 3" => t.Hour is >= 6 and < 18,
        "Valve 1" => t.Minute < 10,
        "Valve 2" => t.Minute is >= 30 and < 40,
        "Fan 1" => t.Hour is >= 10 and < 16,
        _ => false
    };

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
