# Feedback updates — what changed after lecturer feedback

This file lists the changes I made in response to lecturer feedback, and how I checked each one.

## Part 2 feedback (Predictive Action and Suggestion Engine: 18 / 30)

> *“…no explicit `Queue<T>` for query history (uses `List<OperatorEvent>`), and engine execution path is synchronous (no async `Task` processing for suggestion generation).”*

| # | Feedback | Before | After | Where | How it was checked |
|---|---|---|---|---|---|
| 1 | No explicit `Queue<T>` for query history | `private readonly List<OperatorEvent> _recent` holding the last 3 events; `RemoveAt(0)` to drop the oldest | Two explicit bounded FIFO queues: `Queue<OperatorEvent> _queryHistory` (capacity **200**) and `Queue<OperatorEvent> _window` (3-event look-back for the Markov model). New events are enqueued and the oldest dequeued when full (`EnqueueBounded`). `RecentQueries(take)` reads the history newest-first. `queryHistoryDepth` and `queryHistoryCapacity` are in the insights `model`. New endpoint `GET /api/stream/insights/history?take=`. | `Services/Streaming/PredictiveEngine.cs`, `Endpoints/StreamEndpoints.cs` | `test-part3.sh` section 10: capacity reported as 200; depth grows by 1 after a search; history is newest-first; after 210 more events the depth is capped at 200 and `take=500` is clamped to 200 events; default `take` is 20. |
| 2 | Suggestion generation is synchronous | `public InsightsResult GetInsights(...)` did everything in one method on the request thread; `GET /insights` called it directly | `public async Task<InsightsResult> GetInsightsAsync(alerts, CancellationToken)`: one registry snapshot, then two independent halves on `Task.Run` — `BuildSuggestions` (transition model + best-first look-ahead) and `AnalyseDevices` (trend forecast, z-score anomaly, bounded risk heap) — awaited with `Task.WhenAll`, the cancellation token checked between items, then merged. Learning (`Record*`) stays synchronous so event order is preserved. `GET /api/stream/insights` is now `async` and takes the request `CancellationToken` (an abandoned request returns 499 instead of finishing the work). | `Services/Streaming/PredictiveEngine.cs`, `Endpoints/StreamEndpoints.cs` | `test-part2.sh` still passes 35 / 35 (the predictive-engine section calls the same endpoint); `test-part3.sh` checks `GET /insights` returns 200 with the new `model` fields. |

**Part 1 feedback.** No written Part 1 feedback was available when this ledger was written, so there are no Part 1 rows. If feedback exists, add rows here in the same format.

## Other improvements made while doing the final PoE

| Improvement | Why | Where | How it was checked |
|---|---|---|---|
| `EventKind` is serialised as a string (`"Search"`) instead of a number | The new history endpoint was returning `"kind": 1`, which is unreadable in the dashboard and in Swagger | `PredictiveEngine.cs` | `test-part3.sh` asserts `kind == "Search"` for the latest event |
| Spanning-tree and path costs are summed as `decimal` | In a randomised test, Prim and Kruskal totals differed by 0.01 after rounding because they add the same weights in a different order | `MeshGraph.cs` | randomised comparison on 200 random graphs, plus `costsAgree` in `/benchmark/graph` and `test-part3.sh` |
| Part 2 and the mesh share one picture of the network | A node with an active Part 2 *Disconnected* alert is *Down* in the mesh; it recovers when it sends telemetry again | `IngestionPipeline.GetDisconnectedMacs()`, `MeshNetwork.cs` | `test-part3.sh` section 5 |
| Long history back-fill that cannot disturb Part 2 | `SeedHistory` got an `updateLive` flag so the 24 h back-fill goes into the archive without touching a device’s live “recent” window used by the forecaster | `DeviceRegistry.cs`, `DeviceSeeder.cs` | `test-part2.sh` still 35 / 35 |
| Every mesh route resolves nodes the same way | MAC, Part 1 sensor id, device name or `gateway` all work on every `/api/mesh` route | `MeshNetwork.ResolveNode` | `test-part3.sh` sections 2 and 7 |
| README covers all three parts | The README in the Part 2 submission still described Part 1 only (“real-time command stream … not active”) | `README.md` | read through; stale statements removed |
| Part 2 work committed to git | Part 2 code was untracked in the repository, so it had no commit history; it is now committed as a baseline snapshot, followed by one commit per step of the final PoE | git history on `part3-mesh-routing` | `git log --oneline` |
