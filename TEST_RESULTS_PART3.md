# Test results — final PoE (Network Topology & Mesh Routing)

Everything below is the unedited output of the runs described here. Nothing is recorded that was not run.

## How it was run

| Item | Value |
|---|---|
| Date | 12 November 2026 |
| Runtime | .NET SDK 10.0.115 (Ubuntu package), Node.js v22.23.4, bash + curl + python3 |
| Command | `SMARTX_CMD="dotnet <built SmartX.dll>" bash scripts/run-all-tests.sh` |
| Server | started fresh by the script; the three suites run in order against that one start: `test-part2.sh`, `scripts/ui-smoke/ui.js`, `test-part3.sh` |
| Build | `dotnet build` of the project: **0 errors, 0 warnings** (see the note below about Swashbuckle) |

**Build note.** The machine I ran this on could not reach nuget.org, so the `Swashbuckle.AspNetCore` package (Swagger) could not be restored. I built and ran a *copy* of the project with the Swagger package and its three setup lines removed; all of my code is otherwise identical. The real `SmartX.csproj` was therefore not built or run here. Please run `dotnet build` and `./scripts/run-all-tests.sh` on your own machine before submitting.

## Summary

| Suite | Checks | Result |
|---|---|---|
| `scripts/test-part2.sh` (Part 2, must stay 35 / 35) | 35 | **35 passed, 0 failed** |
| `scripts/ui-smoke/ui.js` (real dashboard in jsdom) | 61 | **61 passed, 0 failed** |
| `scripts/test-part3.sh` (final PoE API) | 116 | **116 passed, 0 failed** |

Two other checks were run outside the suites: a randomised test of `AvlTree` (30,000 random inserts and removals compared with `SortedDictionary`, `IsValid()` every ~1,000 operations, 500 random range queries compared with a filter) and of `MeshGraph` (200 random connected graphs: Prim cost equals Kruskal cost with n − 1 edges, articulation points compared with a brute-force “remove each node and re-run BFS”). Both passed. That test project is not part of the repository.

## Full output

```text
################ test-part2.sh
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

################ ui-smoke (jsdom)
== 1. Start screen + navigation
  PASS  three pillar cards on the start screen
  PASS  third pillar is active (not disabled)
  PASS  sidebar module button for the mesh is enabled
  PASS  clicking it opens the app shell
  PASS  hash route is #/mesh/topology
  PASS  nav-mesh is the visible sidebar nav
  PASS  three mesh tabs in the sidebar
== 2. Topology tab
  PASS  map renders 14 nodes
  PASS  six KPI cards
  PASS  13 bold tree links
  PASS  weight labels in background pills
  PASS  nodes are keyboard-focusable
  PASS  Fan 1 carries the amber single-point-of-failure dot
  PASS  tree note says Prim and Kruskal are identical
  PASS  connectivity card shows BFS levels
== 3. Selection + failure simulation
  PASS  inspector shows the selected node
  PASS  Bring back online is disabled while the node is up
  PASS  Valve 1 turns Down on the map
  PASS  Offline KPI shows 1
  PASS  connectivity lists the failover hop for Pump B
  PASS  Bring back online is now enabled
  PASS  server agrees: Valve 1 is Down
== 4. Prim / Kruskal switch
  PASS  tree note switches to Kruskal
  PASS  same total cost under Kruskal
  PASS  show-tree toggle hides bold links
  PASS  and shows them again
== 5. Module round trip preserves state
  PASS  Part 2 module opens
  PASS  mesh polling stopped while hidden
  PASS  Part 1 module opens
  PASS  back on the mesh topology tab
  PASS  algorithm still Kruskal
  PASS  selected node still Valve 1
  PASS  outage still shown after the round trip
  PASS  polling resumed
  PASS  state mirrored to sessionStorage
  PASS  chained polling keeps ticking
== 6. Isolation via Fan 1 (UI)
  PASS  Greenhouse Temp 1 is cut off
  PASS  relay suggestion shown
  PASS  Restore all nodes heals the mesh
== 7. Route tracking
  PASS  routes tab visible
  PASS  datalist has every node
  PASS  chips for each node
  PASS  comparison table fills (4 rows)
  PASS  best-cost row is marked
  PASS  failover note mentions the energy delta
  PASS  route highlighted on the second map
  PASS  a chip tracks another node
== 8. History query + paging
  PASS  history tab visible
  PASS  sensors and meters are listed before actuators
  PASS  strip shows matched / visited / height
  PASS  chart drawn with a line and a threshold line
  PASS  log shows 15 rows (page size)
  PASS  next page loads
  PASS  page 2 differs from page 1
  PASS  actuator history renders (step line, ON/OFF axis)
  PASS  reversed dates are rejected in the UI
== 9. Benchmarks
  PASS  tree benchmark table appears
  PASS  degeneration demo shows 3,000 vs 12
  PASS  graph benchmark table appears
  PASS  Prim and Kruskal costs agree in the table
== 10. Page health
  PASS  no script errors in the page

Result: 61 passed, 0 failed

################ test-part3.sh
== 1. Topology, MST, Prim == Kruskal
  PASS  GET /topology -> 200
  PASS  14 nodes (13 devices + gateway)
  PASS  22 links
  PASS  every RSSI distinct
  PASS  MST has n-1 = 13 edges
  PASS  all nodes Up at start
  PASS  Prim cost == Kruskal cost
  PASS  Prim and Kruskal pick the very same edges
  PASS  kruskal view returns the same edge set
  PASS  unknown algorithm -> 400
  PASS  tree is cheaper than using every link
  PASS  Fan 1 flagged as a single point of failure
  PASS  only Fan 1 is a single point of failure
  PASS  GET /nodes -> 200 with 14 rows
== 2. ResolveNode: MAC, name, sensor id, gateway
  PASS  route by MAC -> 200
  PASS  route by device name -> 200
  PASS  route by compact name -> 200
  PASS  route for gateway -> 200
  PASS  unknown node -> 404
  PASS  history accepts a device name
  PASS  gateway has no history -> 400
== 3. Failover: Valve 1 fails
  PASS  fail Valve 1 -> 200
  PASS  Valve 1 is Down
  PASS  Pump B now routes through Fan 1
  PASS  nobody is cut off (the mesh healed)
  PASS  tree still spans every live node (12 edges)
  PASS  Prim == Kruskal after the failure
  PASS  connectivity lists Valve 1's affected nodes (old -> new hop)
  PASS  route marked rerouted
  PASS  failover note carries the energy delta
  PASS  pre-outage route goes via Valve 1
  PASS  MST, Dijkstra and BFS paths all present
  PASS  Moisture A rerouted over 3 hops (+1 hop)
  PASS  recover Valve 1 -> 200
  PASS  healed: Valve 1 Up again
  PASS  route no longer rerouted
== 4. Isolation: Fan 1 fails
  PASS  fail Fan 1 -> 200
  PASS  both greenhouse temps are cut off
  PASS  cut-off KPI = 2
  PASS  2 sub-networks (DFS)
  PASS  relay suggested for each isolated node
  PASS  isolated node has no MST route
  PASS  note says CUT OFF and names a relay
  PASS  reset -> 200
  PASS  everything Up again
== 5. Part 2 'Disconnected' alert makes a node DOWN in the mesh
  PASS  Part 2 disconnect report accepted
  PASS  Smart Meter 3 shows Down
  PASS  reason names the Part 2 alert
  PASS  recover a node that is down only because of an alert -> 409
  PASS  node is Up again once it sends telemetry
== 6. Guards and status codes
  PASS  fail the gateway -> 400
  PASS  fail unknown node -> 404
  PASS  recover unknown node -> 404
  PASS  recover a node that is up -> 409
  PASS  duplicate link -> 409
  PASS  duplicate link (reversed) -> 409
  PASS  self link -> 409
  PASS  RSSI above -30 -> 400
  PASS  RSSI below -100 -> 400
  PASS  missing RSSI -> 400
  PASS  link to unknown node -> 404
  PASS  bad 'from' date -> 400
  PASS  bad 'to' date -> 400
  PASS  reversed dates -> 400
  PASS  lastMinutes=0 -> 400
  PASS  valid new link -> 200
  PASS  23 links now
  PASS  tree unchanged by a weak spare link (Prim == Kruskal)
== 7. Registered Part 1 sensor is 'Unlinked' until linked
  PASS  Part 1 registration -> 200
  PASS  ResolveNode accepts the Part 1 sensor id
  PASS  unlinked node's route says it needs a link
  PASS  new sensor listed as Unlinked
  PASS  cannot fail an unlinked node -> 409
  PASS  link it into the mesh -> 200
  PASS  it is now Up and routed
  PASS  tree grew to 14 edges (15 nodes)
== 8. AVL range queries
  PASS  history -> 200
  PASS  matched ~ 2 h at 30 s intervals (>= 230)
  PASS  tree height equals the optimal height
  PASS  visited ≈ matches + O(log n)
  PASS  visited far fewer than the tree size
  PASS  page is newest-first
  PASS  page size honoured
  PASS  chart series decimated to <= 240 points
  PASS  chart series is chronological
  PASS  stats are min <= avg <= max
  PASS  critical thresholds included
  PASS  page 2 starts older than page 1 ends
  PASS  page 2 is a different page
  PASS  empty window -> 0 matches, 200
  PASS  actuator history flagged as state device
  PASS  actuator state reports every 5 min (~288/day)
  PASS  GET /history/stats -> 200
  PASS  every per-device AVL tree is valid
  PASS  24 h of seed history present (> 20000 readings)
== 9. Benchmarks
  PASS  tree benchmark -> 200
  PASS  AVL and linear scan return identical results
  PASS  AVL examines far fewer readings than the scan
  PASS  AVL is faster than the linear scan
  PASS  AVL height == optimal height
  PASS  plain BST degenerates to height 3000
  PASS  AVL stays at height 12 for the same 3000 keys
  PASS  graph benchmark -> 200
  PASS  Prim and Kruskal costs agree
  PASS  both are spanning trees (n-1 edges)
  PASS  BFS and DFS reach every node
== 10. Part 2 feedback fixes (Queue history + async insights)
  PASS  GET /insights (async) -> 200
  PASS  insights model exposes queryHistoryCapacity = 200
  PASS  queryHistoryDepth grew by 1 after a search
  PASS  GET /insights/history -> 200 with 3 events
  PASS  history is newest-first
  PASS  latest event is the search just made
  PASS  capacity reported as 200
  PASS  queue is bounded: depth capped at 200 after 210 more events
  PASS  take is clamped to the capacity
  PASS  default take works

Result: 116 passed, 0 failed
```

## Things I could not verify

- **The real `SmartX.csproj` build and Swagger.** See the build note above.
- **How the dashboard looks.** The UI test uses jsdom, which runs the page’s real scripts against the live API but does not lay anything out. No screenshot was taken, so SVG positions, label overlap, colours, dark/light appearance and the responsive breakpoints in `mesh.css` have not been looked at in a real browser.
- **Docker.** `docker-compose.yml` has the new `Mesh__SeedHistoryHours` setting but I did not build or run the image.
- **Windows scripts.** `scripts/test-part2.ps1` and the PowerShell simulator were not run; the Part 3 tests exist only as a bash script.
- **Timing figures** quoted in the README come from single runs on one machine and will differ on yours.
- **Part 1 features** (uploads, validation) were not re-tested beyond what the Part 2 and UI suites touch.
