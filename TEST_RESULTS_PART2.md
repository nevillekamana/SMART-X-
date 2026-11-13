# Part 2 — Test Results

Run date: 2026-10-08 · Build: .NET 10 SDK 10.0.112 · Method: `scripts/test-part2.sh` against a freshly started API
(device simulator stopped), plus a headless-Chrome walkthrough of the dashboard.

## 1. Automated API tests (`scripts/test-part2.sh`)

```
== 1. Device registry (Dictionary)
  PASS  GET /devices -> 200
  PASS  13 seeded devices
  PASS  search 'pump' finds 2 devices
  PASS  category filter -> 6 actuators
  PASS  exact MAC lookup -> 1 device
== 2. Ingest validation
  PASS  valid packet -> 202
  PASS  unknown device -> 404
  PASS  wrong metric for device -> 400
  PASS  moisture 140 % -> 400
  PASS  actuator without state -> 400
== 3. Priority queue overtakes the FIFO queue
  PASS  batch of 12 accepted
  PASS  critical packet routed to Priority lane
  PASS  12 waiting in standard lane
  PASS  1 waiting in priority lane
  PASS  critical (arrived last) processed before the 12 waiting standard packets
== 4. HashSet suppresses duplicate alerts
  PASS  still exactly one OverTemp alert for Temp 2
  PASS  4 repeat readings suppressed
  PASS  alert resolves when reading returns to normal
== 5. Disconnect reports (HashSet)
  PASS  first report is new
  PASS  second report is a duplicate
  PASS  node reconnects when it sends a packet
== 6. Commands + Stack undo
  PASS  Pump A -> ON (200)
  PASS  repeat ON is rejected (409)
  PASS  cannot command a sensor (409)
  PASS  bulk: all valves ON (200)
  PASS  undo stack depth 2
  PASS  undo #1 reverts the bulk command
  PASS  valves are OFF again
  PASS  Pump A still ON
  PASS  undo #2 reverts Pump A
  PASS  undo on empty stack -> 409
== 7. Predictive engine
  PASS  exact-name search is learned
  PASS  'Turn Pump A ON' suggested after Moisture B alert
  PASS  suggestion confidence >= 50 %
  PASS  suggestion disappears once Pump A is already ON

Result: 35 passed, 0 failed
```

## 2. Dashboard walkthrough (headless Chrome, 1440×900 and 390×844)

| Check | Result |
|---|---|
| Start screen shows pillar 1 and 2 active, pillar 3 disabled | Pass |
| Opening Part 2 starts the simulator; 13 device cards render, ~4–12 packets/s processed | Pass |
| Search `pump` → 2 cards; filter *Needs attention* → only alerting devices | Pass |
| *Drought · Moisture B* → card turns Critical, sidebar badge increments, *Turn Pump A ON* suggested (100 % confidence, seen 6×) | Pass |
| Running the suggestion switches Pump A ON, the suggestion disappears, Undo shows `(1)` | Pass |
| Priority demo: critical packet queued last is processed ahead of the 40 routine packets (banner shows it) | Pass |
| Switching to Part 1 and back keeps search text, filtered cards and the running simulator; hash route and browser Back work | Pass |
| Part 1 *Sensors* (13 rows) and *Telemetry* feed still work with Part 2 devices | Pass |
| No JavaScript errors in the console; no horizontal scroll at 390 px | Pass |

## 3. Issues found and fixed while testing

| Found | Fix |
|---|---|
| Forecast raised false alarms by extrapolating tiny/mean-reverting movements (e.g. "Smart Meter 3 → 4000 W in 2.8 min") | Require R² ≥ 0.8, movement ≥ 25 % of remaining distance to the limit, ETA ≤ 10 min |
| Suggestion confidence showed 67 % although 6/6 past incidents matched (skip-gram weights split the denominator) | Simplified to P(next \| current) = co-occurrences ÷ occurrences; the in-progress trigger is excluded from the denominator |
| Priority latency inflated by time spent paused by the operator | Latency measured from the later of *enqueued* and *processor resumed* |
| "Most at risk" listed devices with a negligible signal-only score | A device is ranked only if it has a real reason (alert, trend, anomaly, signal < −75 dBm) |
| Part 1's `TelemetryService` was not thread-safe and would now be written by a background thread | Added locking to every public member |

## 4. Not verified here

* `scripts/test-part2.ps1` and `scripts/device-simulator.ps1` were written to mirror the bash versions but **were not executed** (no PowerShell in the build environment).
* The `Dockerfile` was not built (no Docker in the build environment).
* The real `SmartX.csproj` (which restores Swashbuckle from NuGet) could not be restored offline; the identical source was compiled and run in a scratch project without the Swagger lines.
