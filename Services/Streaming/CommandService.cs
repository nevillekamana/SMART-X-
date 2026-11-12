using SmartX.Models.Streaming;

namespace SmartX.Services.Streaming;

/// <summary>
/// Manual override commands (switch a pump, toggle a relay, shut all valves).
/// Every command that changes something is pushed onto a Stack&lt;CommandRecord&gt;, so "Undo" is simply Pop():
/// last in, first out — exactly the order an operator expects when reverting.
/// A bulk command ("shut down all valves") is ONE stack entry holding every device's previous state,
/// so a single Undo restores them all.
/// </summary>
public class CommandService
{
    private readonly object _gate = new();
    private readonly DeviceRegistry _registry;
    private readonly PredictiveEngine _engine;

    private readonly Stack<CommandRecord> _undoStack = new();
    private readonly List<CommandRecord> _log = new();     // full audit trail (includes undo entries)
    private long _nextId;

    public CommandService(DeviceRegistry registry, PredictiveEngine engine)
    {
        _registry = registry;
        _engine = engine;
    }

    private static bool ParseAction(string? action, out string normalised)
    {
        normalised = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "on" => "On",
            "off" => "Off",
            "toggle" => "Toggle",
            _ => ""
        };
        return normalised != "";
    }

    /// <summary>Command one actuator. Throws ArgumentException / KeyNotFoundException / InvalidOperationException.</summary>
    public CommandRecord Execute(string mac, string? action, string? operatorName)
    {
        if (!ParseAction(action, out var act)) throw new ArgumentException("Action must be On, Off or Toggle.");
        if (!_registry.TryGetView(mac, out var dev)) throw new KeyNotFoundException("Unknown device.");
        if (!dev.Category.Equals("Actuator", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{dev.Name} is a {dev.Category} device and cannot be commanded.");

        CommandRecord rec;
        string resolved;
        lock (_gate)
        {
            // re-read inside the lock so two operators can't both "toggle" from the same stale state
            _registry.TryGetView(mac, out dev);
            bool target = act == "Toggle" ? !dev.IsOn : act == "On";
            resolved = target ? "On" : "Off";
            if (dev.IsOn == target) throw new InvalidOperationException($"{dev.Name} is already {resolved.ToUpper()}.");

            _registry.TrySetActuator(mac, target, out var previous, out var name);
            rec = new CommandRecord
            {
                Id = ++_nextId,
                Kind = "Command",
                Action = resolved,
                Description = $"{name} → {resolved.ToUpper()}",
                Operator = operatorName ?? "operator",
                Targets = new List<CommandTarget> { new(mac, name, previous, target) }
            };
            _undoStack.Push(rec);
            _log.Add(rec);
        }

        _engine.RecordCommand(dev.Name, resolved);
        return rec;
    }

    /// <summary>Command every actuator of a kind (Valve / Pump / Relay / Fan / All).</summary>
    public CommandRecord ExecuteBulk(string? kind, string? action, string? operatorName)
    {
        if (!ParseAction(action, out var act) || act == "Toggle")
            throw new ArgumentException("Bulk action must be On or Off.");
        var k = string.IsNullOrWhiteSpace(kind) ? "All" : kind.Trim();
        var devices = _registry.Actuators(k);
        if (devices.Count == 0) throw new KeyNotFoundException($"No actuators of kind '{k}'.");

        CommandRecord rec;
        lock (_gate)
        {
            bool target = act == "On";
            var targets = new List<CommandTarget>();
            foreach (var d in devices)
            {
                if (_registry.TrySetActuator(d.Mac, target, out var previous, out var name) && previous != target)
                    targets.Add(new CommandTarget(d.Mac, name, previous, target));
            }
            if (targets.Count == 0) throw new InvalidOperationException($"All {k.ToLower()} devices are already {act.ToUpper()}.");

            var label = k.Equals("All", StringComparison.OrdinalIgnoreCase) ? "all actuators" : $"all {k.ToLower()}s";
            rec = new CommandRecord
            {
                Id = ++_nextId,
                Kind = "BulkCommand",
                Action = act,
                Description = $"{label} → {act.ToUpper()} ({targets.Count} device{(targets.Count == 1 ? "" : "s")})",
                Operator = operatorName ?? "operator",
                Targets = targets
            };
            _undoStack.Push(rec);
            _log.Add(rec);
        }

        _engine.RecordCommand($"All {k}s", act);
        return rec;
    }

    /// <summary>Revert the most recent command by popping the stack.</summary>
    public CommandRecord Undo()
    {
        lock (_gate)
        {
            if (!_undoStack.TryPop(out var top)) throw new InvalidOperationException("Nothing to undo.");

            foreach (var t in top.Targets)
                _registry.TrySetActuator(t.Mac, t.Previous, out _, out _);
            top.Undone = true;

            var entry = new CommandRecord
            {
                Id = ++_nextId,
                Kind = "Undo",
                Action = "Undo",
                Description = $"Reverted: {top.Description}",
                Operator = top.Operator,
                Targets = top.Targets.Select(t => new CommandTarget(t.Mac, t.Name, t.Applied, t.Previous)).ToList(),
                UndoOf = top.Id
            };
            _log.Add(entry);
            return entry;
        }
    }

    public object GetSnapshot()
    {
        lock (_gate)
        {
            _undoStack.TryPeek(out var top);
            return new
            {
                undoDepth = _undoStack.Count,
                nextUndo = top?.Description,
                undoStack = _undoStack.Take(8).Select(c => new { c.Id, c.Description, c.Timestamp }).ToList(),
                history = _log.AsEnumerable().Reverse().Take(100).ToList(),
                total = _log.Count
            };
        }
    }
}
