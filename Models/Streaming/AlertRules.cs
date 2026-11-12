namespace SmartX.Models.Streaming;

/// <summary>
/// Thresholds and validation rules for the real-time stream.
/// Alert evaluation is a pure function of one reading, which is what lets the ingest endpoint
/// decide "priority lane or standard lane" before the packet is even queued.
/// </summary>
public static class AlertRules
{
    public const double MoistureCriticalLow = 20;      // % — hydroponics node drying out
    public const double PowerCriticalHigh = 4000;      // W — smart-meter power spike (same default as Part 1)
    public const double TemperatureCriticalHigh = 45;  // °C — over-temperature
    public const int WeakSignalDbm = -80;

    public static readonly string[] Metrics = { "temperature", "moisture", "power", "state" };

    public static string UnitFor(string metric) => metric switch
    {
        "temperature" => "°C",
        "moisture" => "%",
        "power" => "W",
        _ => "state"
    };

    public static string DefaultMetricFor(string category) => category.ToLowerInvariant() switch
    {
        "environmental" => "temperature",
        "powerconsumption" => "power",
        _ => "state"
    };

    public static bool MetricMatchesCategory(string category, string metric) => category.ToLowerInvariant() switch
    {
        "environmental" => metric is "temperature" or "moisture",
        "powerconsumption" => metric == "power",
        "actuator" => metric == "state",
        _ => false
    };

    public static (double? Low, double? High) ThresholdsFor(string metric) => metric switch
    {
        "moisture" => (MoistureCriticalLow, null),
        "power" => (null, PowerCriticalHigh),
        "temperature" => (null, TemperatureCriticalHigh),
        _ => (null, null)
    };

    /// <summary>All alert types a reading of this metric can raise or clear.</summary>
    public static string[] TypesFor(string metric) => metric switch
    {
        "moisture" => new[] { "MoistureLow", "WeakSignal" },
        "power" => new[] { "PowerSpike", "WeakSignal" },
        "temperature" => new[] { "OverTemp", "WeakSignal" },
        _ => new[] { "WeakSignal" }
    };

    /// <summary>Value sanity limits — rejects garbage before it reaches the queues.</summary>
    public static string? ValidateValue(string metric, double value) => metric switch
    {
        _ when double.IsNaN(value) || double.IsInfinity(value) => "Value cannot be NaN or Infinity.",
        "moisture" when value < 0 || value > 100 => "Moisture must be between 0 and 100 %.",
        "temperature" when value < -60 || value > 200 => "Temperature must be between -60 and 200 °C.",
        "power" when value < 0 || value > 100000 => "Power must be between 0 and 100000 W.",
        _ => null
    };

    public static List<AlertCandidate> Evaluate(string metric, double value, int signal, string deviceName)
    {
        var list = new List<AlertCandidate>();
        switch (metric)
        {
            case "moisture" when value < MoistureCriticalLow:
                list.Add(new("MoistureLow", AlertSeverity.Critical,
                    $"{deviceName} moisture {value:0.#}% is below the critical floor of {MoistureCriticalLow}%.", 0));
                break;
            case "power" when value > PowerCriticalHigh:
                list.Add(new("PowerSpike", AlertSeverity.Critical,
                    $"{deviceName} drew {value:0.#} W, above the {PowerCriticalHigh} W limit.", 0));
                break;
            case "temperature" when value > TemperatureCriticalHigh:
                list.Add(new("OverTemp", AlertSeverity.Critical,
                    $"{deviceName} reads {value:0.#} °C, above the {TemperatureCriticalHigh} °C limit.", 1));
                break;
        }

        if (signal < WeakSignalDbm)
            list.Add(new("WeakSignal", AlertSeverity.Warning,
                $"{deviceName} signal is {signal} dBm (weaker than {WeakSignalDbm} dBm).", 50));

        return list;
    }

    public static string Humanise(string alertType) => alertType switch
    {
        "MoistureLow" => "moisture critically low",
        "PowerSpike" => "power spike",
        "OverTemp" => "over-temperature",
        "WeakSignal" => "weak signal",
        "Disconnected" => "disconnected",
        _ => alertType
    };
}
