# Smart-X – IoT Ingestion, Real-Time Command Stream and Mesh Routing
**PROG7312 (Programming 3B) – Part 1, Part 2 and Final PoE**

Smart-X is an ASP.NET Core 10 Minimal API project with a plain HTML, CSS and JavaScript dashboard (no front-end framework), built up over three parts of PROG7312: Part 1 (sensor ingestion and validation), Part 2 (real-time command stream) and the final PoE (network topology and mesh routing).

The main purpose of the project is to work with simulated IoT sensor data. The application can register sensors, receive and validate telemetry, store readings in memory, handle file attachments and validate a deployment tree using recursion.

**All three pillars on the start screen are active.** The sections after “Running the Project” explain Part 2 briefly and the final PoE in depth, and the [final-PoE section](#final-poe--network-topology--mesh-routing) shows how each data structure helps the topology page, with measured numbers.

---

## Technologies Used

| Part | Technology |
|---|---|
| Backend | ASP.NET Core 10 Minimal APIs |
| API documentation | Swagger / Swashbuckle |
| Frontend | HTML5, CSS3 and JavaScript |
| Storage | In-memory collections: `Dictionary`, `SortedDictionary`, `Queue`, `PriorityQueue`, `Stack`, `HashSet`, a hand-written AVL tree and a graph adjacency list |

The dashboard is inside the `wwwroot` folder, so the same ASP.NET Core application serves both the API and the frontend. This means I only need to run one application.

## Video demos

- Part 1 demo: https://www.youtube.com/watch?v=NkO0CCrsWm8
- Part 2: <ADD LINK>
- Final PoE demo: https://youtu.be/FZaUta7NX-g?si=vQKsAUQ7zAVPBf99

---

## Requirements

Before running the project, make sure you have:

- .NET 10 SDK
- A modern web browser such as Chrome, Edge or Firefox
- No database is needed. Docker is optional (`docker compose up --build`). Node.js is only needed for the optional UI smoke test in `scripts/ui-smoke`

To check the installed .NET version, run:

```bash
dotnet --version
```

The version should be `10.x.x`.

---

## Running the Project

### 1. Clone the repository

```bash
git clone https://github.com/<your-username>/<your-repo>.git
cd <your-repo>
```

### 2. Restore the packages

```bash
dotnet restore
```

### 3. Build the project

```bash
dotnet build
```

### 4. Run the project

```bash
dotnet run
```

The application should start using the URLs in `Properties/launchSettings.json`.

The current development ports are:

- `https://localhost:59184`
- `http://localhost:59185`

The ports can be different on another computer, so the console output should be checked after running the application.

### 5. Open the dashboard

Open the HTTPS address shown in the console. The Smart-X start page should appear.

From there, select **Sensor Data Ingestion & Telemetry** to open the main dashboard.

### 6. Swagger

Swagger can also be used to test the API without using the dashboard:

```text
https://localhost:<port>/swagger
```

---

## Project Structure

```text
SmartX/
├── Program.cs                     API setup, Part 1 endpoints, DI registration, MapStreamEndpoints / MapMeshEndpoints
├── Models/
│   ├── TelemetryPacket.cs  SensorData.cs  SensorRegistration.cs  DeploymentNode.cs
│   └── Streaming/                 Part 2 DTOs, alert rules
├── Services/
│   ├── TelemetryService.cs  DeploymentValidationService.cs  FileUploadService.cs      (Part 1)
│   ├── Streaming/                 (Part 2)
│   │   ├── DeviceRegistry.cs        Dictionary by MAC + SortedDictionary timelines; feeds the AVL archive
│   │   ├── IngestionPipeline.cs     Queue + PriorityQueue + HashSet (alerts)
│   │   ├── CommandService.cs        Stack (undo)
│   │   ├── PredictiveEngine.cs      bounded Queue history, Markov model, async insights
│   │   └── DeviceSeeder.cs          13 demo devices + Mesh:SeedHistoryHours of history
│   └── Mesh/                      (final PoE)
│       ├── AvlTree.cs               self-balancing BST written from scratch
│       ├── TelemetryHistoryIndex.cs one AVL tree per device, range queries, paging, decimation
│       ├── MeshGraph.cs             adjacency list, BFS, DFS, Tarjan, Prim, Kruskal, Dijkstra
│       ├── MeshNetwork.cs           the demo mesh, outages, topology / connectivity / route reports
│       └── MeshBenchmarks.cs        measured AVL and graph benchmarks
├── Endpoints/
│   ├── StreamEndpoints.cs         /api/stream/*
│   └── MeshEndpoints.cs           /api/mesh/*
├── wwwroot/
│   ├── index.html
│   ├── css/  dashboard.css  command-stream.css  mesh.css
│   └── js/   dashboard.js   commandStream.js    mesh.js
├── scripts/
│   ├── test-part2.sh  test-part3.sh   API tests (bash + curl + python3)
│   ├── ui-smoke/ui.js                 jsdom test that drives the real dashboard
│   └── device-simulator.sh / .ps1     sends packets like a device would
├── FEEDBACK_UPDATES.md            ledger of lecturer-feedback changes
├── TEST_RESULTS_PART3.md          real output of the three test suites
├── Dockerfile  docker-compose.yml
└── README.md
```

`Program.cs` contains the API setup and endpoint definitions.

The files in `Models` contain the main data structures used by the application.

The files in `Services` contain most of the application logic, such as telemetry handling, deployment validation and file uploads.

The `wwwroot` folder contains the dashboard files.

---

## Main C# Concepts Used

The project was also used to demonstrate some of the C# concepts required for the assignment.

### Generics

`TelemetryPacket<T>` is a generic class used for different sensor values. It is used with values such as:

- `float` for environmental readings
- `int` for power readings
- `bool` for actuator readings

It also implements the `ITelemetryPacket` interface.

### Operator Overloading

The `SensorData` class has overloaded operators such as:

```text
+
-
>
<
>=
<=
```

These are used when comparing or combining readings from sensors.

For example, two compatible sensor readings can be added using the `+` operator.

### Arrays and Lists

Telemetry readings are first collected into a temporary batch. After 10 readings, the batch is stored in a jagged `double[][]` array.

The readings are also kept in a `List<double>` for easier access when displaying the history.

### HashSet

`HashSet<double>` is used when consolidating the telemetry batches. This removes duplicate values without having to repeatedly search through a `List`.

### Recursion

The deployment structure is represented as a tree. The `DeploymentValidationService` uses recursion to:

- validate the tree
- search for nodes
- count nodes
- display the deployment structure

The tree has levels such as:

```text
Facility
  └── Zone
      └── Sub-Zone
          └── Room
              └── Node
```

### Sensor Validation

Before telemetry is stored, the application checks that:

1. The sensor has been registered.
2. The sensor ID exists.
3. The telemetry type matches the sensor category.

For example, a power reading should not be accepted for a sensor registered as an environmental sensor.

---

## Validation and Security

I added a number of checks to prevent incorrect data from being accepted.

### Telemetry Validation

The telemetry endpoint checks the sensor ID and category before storing the reading.

An unknown sensor results in a `404 Not Found`, while a category mismatch results in a `400 Bad Request`.

### Sensor Registration

Sensor registration checks required fields and makes sure that a MAC address is not already being used.

### JSON Validation

Malformed JSON requests are caught and return a `400 Bad Request` instead of exposing an exception or stack trace.

### Number Checks

Environmental readings reject `NaN` and `Infinity`.

Power consumption readings cannot be negative.

### File Uploads

Uploaded files are checked before they are saved. The application checks:

- allowed file extensions
- content type
- file size
- file name
- file signatures (magic bytes) for supported file types

PNG, JPEG and PDF files are checked using their file signatures instead of only trusting the file extension.

### Exception Handling

A global exception handler is used so that unexpected errors return a simple JSON error instead of exposing the application's stack trace.

---

## API Endpoints

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/sensors/register` | Register a sensor |
| GET | `/api/sensors` | Get all registered sensors |
| GET | `/api/sensors/{id}` | Get a sensor by ID |
| GET | `/api/sensors/mac/{mac}` | Get a sensor by MAC address |
| POST | `/api/sensors/{id}/attachments` | Add a file attachment |
| GET | `/api/sensors/{id}/attachments` | Get sensor attachments |
| POST | `/api/telemetry/push/{type}` | Send telemetry data |
| GET | `/api/telemetry/latest` | Get the latest 50 readings |
| GET | `/api/telemetry/history` | Get telemetry history |
| GET | `/api/telemetry/consolidate` | Get deduplicated telemetry |
| POST | `/api/telemetry/aggregate` | Add two sensor readings |
| POST | `/api/telemetry/delta` | Find the difference between readings |
| POST | `/api/telemetry/compare` | Compare two sensor readings |
| POST | `/api/telemetry/simulate` | Generate simulated readings |
| GET | `/api/deployment/tree` | Display the deployment tree |
| POST | `/api/deployment/validate` | Validate the deployment tree |
| GET | `/api/deployment/find/{name}` | Find a deployment node |

**Part 2 — `/api/stream`** (all JSON)

| Method | Route | Purpose |
|---|---|---|
| GET | `/devices?q=&category=&state=` | Search / filter live devices (Dictionary lookups) |
| GET | `/devices/{id}` · `/devices/{id}/history` | One device · its SortedDictionary timeline |
| POST | `/ingest` · `/ingest/batch` | What a device calls; routed to the standard or priority lane |
| GET | `/queues` · POST `/processor/pause` `/processor/resume` | Pipeline monitor |
| GET | `/alerts` · POST `/disconnect-reports` | HashSet-backed active alerts |
| POST | `/commands` · `/commands/bulk` · `/commands/undo` · GET `/commands` | Manual overrides + Stack undo |
| POST | `/events/search` | Log an operator search for the predictive engine |
| GET | `/insights` | **Async** suggested actions, forecasts, anomalies, risk ranking |
| GET | `/insights/history?take=` | The bounded `Queue<OperatorEvent>` (newest first, max 200) |

**Final PoE — `/api/mesh`**

| Method | Route | Purpose |
|---|---|---|
| GET | `/topology?algorithm=prim\|kruskal` | Nodes, links, minimum spanning tree, KPIs |
| GET | `/connectivity` | BFS levels, failover hops, isolated nodes + relay, DFS sub-networks, single points of failure |
| GET | `/routes/{id}` | MST / Dijkstra / BFS path for a node, pre-outage route, `rerouted`, `failoverNote` |
| GET | `/nodes` | Every node with status (Up, Down, Isolated, Unlinked) |
| POST | `/nodes/{id}/fail` · `/nodes/{id}/recover` · `/outages/reset` | Simulate an outage, bring a node back, restore all |
| POST | `/links` `{a, b, rssi}` | Add a wireless link (also joins an *Unlinked* Part 1 sensor to the mesh) |
| GET | `/nodes/{id}/history?from&to&lastMinutes&page&pageSize` | AVL range query + stats, chart series, tree statistics |
| GET | `/history/stats` | Size, height, rotations and validity of every per-device AVL tree |
| GET | `/benchmark/tree?points&queries` · `/benchmark/graph?nodes` | Measured benchmarks |

`{id}` accepts a MAC address, a Part 1 sensor id, a device name (`Valve 1` or `valve1`) or `gateway` on **every** mesh route.

| Situation | Status |
|---|---|
| Fail the gateway · RSSI outside −100…−30 · bad or reversed dates · unknown `algorithm` | 400 |
| Unknown node | 404 |
| Duplicate or self link · recover a node that is up · recover a node that is down only because of a Part 2 alert · fail an unlinked node | 409 |

The full request and response information can also be viewed through Swagger after the application has been started.

---

## Testing the Application

### Automated suites (Part 2 and final PoE)

Start the API fresh (`dotnet run`), then run the suites **in this order** against that one start (the final-PoE suite registers a Part 1 sensor and fills the 200-event queue, so each suite is meant to be run once per start):

```bash
./scripts/test-part2.sh                     # 35 checks: queues, dictionary, HashSet, stack undo, predictive engine
(cd scripts/ui-smoke && npm install && node ui.js)   # 61 checks: drives the real dashboard in jsdom
./scripts/test-part3.sh                     # 116 checks: topology, failover, AVL, benchmarks, guards, Part 2 feedback fixes
```

`./scripts/run-all-tests.sh` does all of this for you: it starts a fresh API, runs the three suites in order and stops the API.

Each takes the base URL as an optional argument (default `http://localhost:59185`). Real outputs are in `TEST_RESULTS_PART3.md`.

### Manual checks (Part 1)

I tested the main functions by running the application locally and using the dashboard and API endpoints.

The basic testing process was:

1. Run the application using `dotnet run`.
2. Open the dashboard.
3. Use **Simulate activity** to create telemetry readings.
4. Register a sensor from the Sensors section.
5. Upload a file attachment to the sensor.
6. Send telemetry manually or generate more simulated readings.
7. Run the deployment validation.
8. Test the aggregate, delta and compare functions using two sensors.

The deployment test also checks the intentionally over-capacity `Node A2`.

---

## Test Results

### Telemetry Validation

| Test | Expected result | Result |
|---|---|---|
| Push telemetry for registered sensor | `200 OK` | PASS |
| Push telemetry for unregistered sensor | `404 Not Found` | PASS |
| Push telemetry with wrong category | `400 Bad Request` | PASS |
| Send malformed JSON in Development | `400 Bad Request` | PASS |
| Send malformed JSON in Production | `400 Bad Request` | PASS |

### Consolidation

| Test | Expected result | Result |
|---|---|---|
| Consolidate telemetry | Unique values returned | PASS |
| Store 10+ readings in batches | At least one batch created | PASS |

### Operator Overloading

| Test | Expected result | Result |
|---|---|---|
| Aggregate two compatible sensors | Sum returned | PASS |
| Aggregate missing sensor | `404 Not Found` | PASS |
| Missing sensor ID | `400 Bad Request` | PASS |
| Calculate delta | Difference returned | PASS |
| Compare compatible sensors | Comparison returned | PASS |
| Compare sensors with different units | `400 Bad Request` | PASS |

### File Uploads

| Test | Expected result | Result |
|---|---|---|
| Upload valid PDF | Accepted | PASS |
| Upload valid PNG | Accepted | PASS |
| Upload valid JPEG | Accepted | PASS |
| Upload PNG with incorrect file signature | Rejected | PASS |
| Upload `.log` using `application/octet-stream` | Accepted | PASS |
| Upload JSON with a content-type parameter | Accepted | PASS |

### Deployment Tree

| Test | Expected result | Result |
|---|---|---|
| Render deployment tree | Tree returned | PASS |
| Validate deployment tree | Invalid/over-capacity nodes detected | PASS |
| Find a deployment node | Node returned | PASS |

### Overall Result

The tests recorded for Part 1 resulted in:

**17/17 critical test scenarios passed.**

The tests also confirmed that:

- stack traces were not returned in the tested Development and Production scenarios
- validation errors returned the expected HTTP status codes
- file signature checking worked
- operator overloading worked with unit checking
- recursive deployment validation still worked

---

## Part 2 — Real-Time Command Stream (summary)

| Requirement | Structure | Where |
|---|---|---|
| FIFO ingestion of every packet | `Queue<StreamPacket>` | `IngestionPipeline` |
| Critical alerts overtake the queue | `PriorityQueue<StreamPacket,(int Urgency, long Seq)>` drained first | `IngestionPipeline` |
| Undo manual overrides | `Stack<CommandRecord>` | `CommandService` |
| O(1) device lookup by MAC | `Dictionary<string, LiveDevice>` | `DeviceRegistry` |
| Timelines in order | `SortedDictionary<long, ReadingPoint>` per device | `DeviceRegistry` |
| No duplicate alerts | `HashSet<string>` of `MAC|AlertType` keys | `IngestionPipeline` |
| Predictive suggestions | Markov transition model + best-first look-ahead + trend forecast + bounded risk heap | `PredictiveEngine` |

To see a high-priority alert jump the queue: open **Pipeline monitor**, click **Run priority demo**, or run `./scripts/test-part2.sh`.

---

## Final PoE — Network Topology & Mesh Routing

### What it models

In a **mesh network** each ESP32 node can hear several neighbours, so a packet hops node to node until it reaches the gateway, and the network heals itself when a node dies. The app models that with a graph: **vertices** are nodes (13 demo devices + `GATEWAY`), **edges** are wireless links (22 in the demo), and every link has a **power cost** derived from its signal strength (RSSI):

```csharp
// MeshGraph.cs — 1 at −40 dBm or better, growing quadratically as the signal weakens
public static double PowerCost(int rssi) => Math.Round(1 + Math.Pow(Math.Max(0, (-rssi - 40) / 10.0), 2), 2);
```

| RSSI | −52 dBm | −60 dBm | −75 dBm | −79 dBm |
|---|---|---|---|---|
| Cost | 2.44 | 5.00 | 13.25 | 16.21 |

The demo layout has four clusters (hydroponics bay 1, bay 2, greenhouse, power room). Every link has a **distinct** RSSI, so link costs are distinct, so the minimum spanning tree is unique and Prim and Kruskal must return exactly the same tree. **Fan 1 is the only way out of the greenhouse**, which makes it a deliberate single point of failure.

### Using it

1. Open the start screen and pick **Network Topology & Mesh Routing** (or go to `#/mesh/topology`).
2. **Topology tab** — the map shows the gateway (square) and nodes coloured *Up*, *Down*, *Cut off* or *Unlinked*. Bold links with cost labels are the spanning tree; an amber dot marks a single point of failure. Switch **Prim / Kruskal** or hide the tree. Click (or Tab + Enter on) a node to inspect it, then **Simulate failure** / **Bring back online**. **Restore all nodes** clears every simulated outage.
3. **Routes tab** — type a MAC, sensor id or name (or click a chip) to compare the MST path, the Dijkstra path and the BFS path, see the route from before the outage, and read the failover note.
4. **History tab** — pick a sensor, choose a window (or a preset) and read the AVL strip: how many readings matched and how few tree nodes were visited. Page through the log, and run the two benchmarks.
5. A node is **Down** if you simulated an outage **or** Part 2 has an active *Disconnected* alert for it (for example when the Part 2 device simulator stops sending), so the three pillars share one picture of the network.

### How each data structure helps

#### 1. AVL tree — historical telemetry queries (`Services/Mesh/AvlTree.cs`, `TelemetryHistoryIndex.cs`)

**Role.** Each device gets its own AVL tree keyed by timestamp ticks. “All readings for Smart Meter 1 between 14:00 and 16:00” walks only the part of the tree that can hold those keys instead of scanning every stored reading. A plain BST would be useless here: telemetry arrives in time order, which turns an unbalanced BST into a linked list. The AVL tree repairs itself with rotations (left, right, left-right, right-left) after every insert and delete, keeping the height near log₂ n.

```csharp
// AvlTree.cs — rebalance one node after its sub-trees changed height
private Node Rebalance(Node n)
{
    Update(n);
    int b = Balance(n);
    if (b > 1)                                                   // left-heavy
    {
        if (Balance(n.Left) < 0) n.Left = RotateLeft(n.Left!);   // left-right: rotate the child first
        return RotateRight(n);                                   // left-left
    }
    if (b < -1)                                                  // right-heavy
    {
        if (Balance(n.Right) > 0) n.Right = RotateRight(n.Right!); // right-left
        return RotateLeft(n);                                      // right-right
    }
    return n;
}
```

The range query prunes: if a node’s key is below `from`, its whole left sub-tree is skipped; if above `to`, its right sub-tree is skipped.

```csharp
private void VisitRange(Node? n, TKey from, TKey to, Action<TKey, TValue> visit, ref int visited)
{
    if (n is null) return;
    visited++;
    int cFrom = _cmp.Compare(n.Key, from);
    int cTo = _cmp.Compare(n.Key, to);
    if (cFrom > 0) VisitRange(n.Left, from, to, visit, ref visited);   // left may still hold keys ≥ from
    if (cFrom >= 0 && cTo <= 0) visit(n.Key, n.Value);
    if (cTo < 0) VisitRange(n.Right, from, to, visit, ref visited);    // right may still hold keys ≤ to
}
```

`IsValid()` checks BST ordering, every stored height and that |balance| ≤ 1 everywhere; `GET /api/mesh/history/stats` reports it for every tree. The archive is fed by `DeviceRegistry` on every accepted reading (and the seeder), is capped at 50,000 readings per device (the oldest is evicted), and the seeder back-fills `Mesh:SeedHistoryHours` (default 24) of history first: one reading per 30 s for sensors and meters, one state report per 5 min for actuators.

**Measured** (`GET /api/mesh/benchmark/tree`, 200 random 2-hour windows over one reading every ~5 s; results of both methods verified identical; one run on the development machine, timings vary by machine):

| Readings stored | AVL total | Linear scan total | Speed-up | Nodes the AVL visited per query | Matches per query | Height (minimum) |
|---|---|---|---|---|---|---|
| 10,000 | 15.5 ms | 14.8 ms | 1.0× | 1,455.6 | 1,443.1 | 14 (14) |
| 50,000 | 4.8 ms | 71.5 ms | 15.0× | 1,455.4 | 1,440.0 | 16 (16) |
| 200,000 | 4.3 ms | 309.6 ms | 72.1× | 1,458.1 | 1,440.5 | 18 (18) |

The AVL cost stays flat (it only touches the matches plus about 15 nodes) while the scan grows with the data. At 10,000 readings a 2-hour window covers about 14% of the data, so the tree has no advantage there; the benefit appears as the history grows, which is the point of the structure. On the live seed data, a 2-hour query on Smart Meter 1 matched 259 readings and visited 265 of 2,900 tree nodes, at height 12 (minimum 12).

**Why balancing matters** (same endpoint): 3,000 sorted inserts give a plain BST of height **3,000** and an AVL tree of height **12** (minimum 12).

#### 2. Graph (adjacency list) — the mesh (`Services/Mesh/MeshGraph.cs`)

**Role.** `Dictionary<vertex, List<MeshEdge>>`: listing a node’s neighbours costs O(degree) rather than O(V), which suits a sparse mesh where each node hears a few neighbours. Vertex ids are case-insensitive, and **every algorithm takes an optional `blocked` set of down nodes**, so an outage is simulated by *ignoring* those nodes rather than editing the topology.

```csharp
public bool AddEdge(MeshEdge e)
{
    if (e.A.Equals(e.B, StringComparison.OrdinalIgnoreCase)) return false;   // no self-links
    if (!_adj.ContainsKey(e.A) || !_adj.ContainsKey(e.B)) return false;
    if (!_pairs.Add(PairKey(e.A, e.B))) return false;                        // no duplicate links
    _edges.Add(e); _adj[e.A].Add(e); _adj[e.B].Add(e);
    return true;
}
```

#### 3. BFS (`Queue`) — hop levels and “where do I hop instead?”

**Role.** A queue explores the mesh level by level, so the first time a node is reached is by the fewest hops. The connectivity card shows the BFS levels from the gateway, and the route table’s *BFS* row is the hop-minimal path.

```csharp
var queue = new Queue<string>();
hops[start] = 0; parent[start] = null; queue.Enqueue(start);
while (queue.Count > 0)
{
    var v = queue.Dequeue();
    order.Add(v);
    foreach (var e in _adj[v])
    {
        var w = e.Other(v);
        if (Down(blocked, w) || hops.ContainsKey(w)) continue;     // skip down nodes and visited nodes
        hops[w] = hops[v] + 1; parent[w] = v; queue.Enqueue(w);
    }
}
```

#### 4. DFS (`Stack`) and Tarjan — sub-networks and single points of failure

**Role.** Iterative DFS with an explicit `Stack<string>` (no recursion, so no stack overflow on deep meshes) collects each group of nodes that can still reach each other: the *sub-networks* after a failure. The same graph also runs **Tarjan’s articulation-point algorithm** from the gateway to find nodes whose loss would disconnect others: a non-root node `p` is a single point of failure when some child `v` has `low[v] >= disc[p]`.

```csharp
if (!p.Equals(r, StringComparison.OrdinalIgnoreCase) && low[v] >= disc[p]) result.Add(p);
```

In the demo, Fan 1 is the only single point of failure and the connectivity card says it would cut off both greenhouse temperature sensors.

#### 5. Minimum spanning tree — Prim and Kruskal

**Role.** The MST connects every live node to the gateway using the smallest possible total power cost, which is the “most power-efficient routing” the brief asks for. Each node stores only its **next hop** toward the gateway (`RootTree`), which is what a real mesh node keeps.

*Prim* grows the tree from the gateway with a `PriorityQueue` that always offers the cheapest link leaving the tree. The priority is `(weight, edgeId)` so ties resolve the same way on every run. .NET's `PriorityQueue` is a binary min-heap with O(log E) extraction and offers O(E log V) total complexity.

```csharp
var pq = new PriorityQueue<(MeshEdge Edge, string To), (double, string)>(new EdgeOrder());
...
var (e, to) = pq.Dequeue();
if (!inTree.Add(to)) continue;            // that end is already connected: this link would close a cycle
edges.Add(e);
```

*Kruskal* sorts the links and takes each one whose ends are in different sets of a **union-find** (path compression + union by rank). When outages split the mesh it returns a *forest*, so `MeshNetwork` keeps only the links in the gateway’s component.

```csharp
public bool Union(string a, string b)
{
    var ra = Find(a); var rb = Find(b);
    if (ra.Equals(rb, StringComparison.OrdinalIgnoreCase)) return false;   // same set: would close a cycle
    if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);                        // union by rank
    _parent[rb] = ra;
    if (_rank[ra] == _rank[rb]) _rank[ra]++;
    return true;
}
```

Totals are summed as `decimal` so Prim and Kruskal costs compare exactly (a floating-point version disagreed in the last digit in my own randomised test).

**Measured, demo mesh (healthy):** tree cost **52.65** against **154.70** if every link transmitted: **102.05 saved (66%)**. Prim and Kruskal return the same 13 links.

**Dijkstra baseline.** The MST minimises the cost of the *whole tree*, not of each node’s own route, so Dijkstra is shown beside it. In the healthy demo the two agree for 11 of 13 nodes; they differ for Relay 3 (MST 8.53, Dijkstra 7.76) and Smart Meter 3 (MST 15.29, Dijkstra 14.52): sharing one tree costs those two nodes a little extra, while the tree as a whole is the cheapest.

**Measured, random connected meshes** (`GET /api/mesh/benchmark/graph`, links ≈ 3× nodes, one run; both spanning trees have n − 1 edges and equal cost every time):

| Nodes | Links | Prim | Kruskal | BFS | DFS | Dijkstra | Tarjan | Costs agree |
|---|---|---|---|---|---|---|---|---|
| 500 | 1,483 | 8.9 ms | 5.6 ms | 0.3 ms | 0.9 ms | 1.0 ms | 0.7 ms | yes |
| 2,000 | 5,992 | 53.3 ms | 7.3 ms | 1.7 ms | 3.5 ms | 5.6 ms | 3.5 ms | yes |
| 10,000 | 29,986 | 148.9 ms | 39.7 ms | 9.7 ms | 17.3 ms | 44.2 ms | 28.5 ms | yes |

Kruskal was faster than Prim in these runs, probably because Prim’s heap compares tuples with a string tie-break on every operation; I did not profile it further.

#### 6. Self-healing — what the demo shows (measured on the demo mesh)

| Scenario | Result |
|---|---|
| **Valve 1 fails** | Pump B reroutes via Fan 1: *+11.53 power units (+173%)*. Moisture Sensor A reroutes Pump B → Fan 1 → Gateway: *+20.81 (+366%), +1 hop*. Nobody is cut off; tree cost 52.65 → 67.22 over 12 live nodes. |
| **Fan 1 fails** | Greenhouse Temp 1 and 2 are **cut off** (2 sub-networks). The relay suggestions are Smart Meter 1 and Smart Meter 2, the nearest live nodes on the map. |
| **Part 2 disconnect alert** | `POST /api/stream/disconnect-reports` for a device marks it *Down* in the mesh; it is *Up* again once it sends telemetry. |

### Part 2 feedback fixed in this version

The Part 2 predictive engine lost marks for using a `List<OperatorEvent>` instead of an explicit `Queue<T>` and for synchronous suggestion generation. Both are fixed (details and checks in `FEEDBACK_UPDATES.md`):

```csharp
private readonly Queue<OperatorEvent> _queryHistory = new(QueryHistoryCapacity + 1);   // capacity 200
private readonly Queue<OperatorEvent> _window = new(MaxLookback + 1);                  // 3-event look-back

private static void EnqueueBounded(Queue<OperatorEvent> queue, OperatorEvent e, int capacity)
{
    queue.Enqueue(e);
    while (queue.Count > capacity) queue.Dequeue();     // drop the oldest
}

public async Task<InsightsResult> GetInsightsAsync(IReadOnlyList<AlertRecord> activeAlerts, CancellationToken ct = default)
{
    var devices = _registry.Snapshot();                                              // one snapshot
    var suggestionsTask = Task.Run(() => BuildSuggestions(activeAlerts, devices, ct), ct);   // transition model + look-ahead
    var analysisTask = Task.Run(() => AnalyseDevices(activeAlerts, devices, ct), ct);        // forecast, z-score, risk heap
    await Task.WhenAll(suggestionsTask, analysisTask);
    ...                                                                              // merge
}
```

### Rubric map (Part 3)

| Criterion | Where |
|---|---|
| Tree structures and historical queries (30) | `AvlTree.cs`, `TelemetryHistoryIndex.cs`, History tab, `/api/mesh/nodes/{id}/history`, `/benchmark/tree` |
| Graphs, traversals and MST (30) | `MeshGraph.cs`, `MeshNetwork.cs`, Topology and Routes tabs, `/topology`, `/connectivity`, `/routes/{id}` |
| Implementation report: README and code explanations (10) | This file |
| Iterative feedback incorporation (5) | `FEEDBACK_UPDATES.md` |
| Main menu integration and architecture health (5) | Start screen + sidebar, `dashboard.js` router, `scripts/ui-smoke/ui.js` |
| Project completion report (10) and technology recommendations (10) | Separate PDF report (not part of this repository’s code) |

---

## Limitations

There are a few limitations with the current version:

- The data is stored in memory, so it is lost when the application is restarted.
- There is no external database in this version.
- The application is mainly intended for the assignment/demo and is not a production-ready storage system.
- The mesh is a simulation: RSSI values are fixed demo numbers, not radio measurements, and a “failed” node is simulated by excluding it from the algorithms. Links added through the dashboard are kept in memory only.
- The minimum spanning tree is recomputed on every request (cheap for 14 nodes, measured at ~150 ms for 10,000); it is not maintained incrementally.
- “Nearest live node” relay suggestions use distance on the map layout, which stands in for physical distance.
- Single points of failure are computed with the gateway as the root, so they describe reachability of the gateway only.
- History is capped at 50,000 readings per device and the chart shows at most 240 min/max-decimated points.
- The UI smoke test uses jsdom, not a real browser, so it checks behaviour and DOM output but not how the page looks.
- Tests must be run on a freshly started API (see “Testing the Application”).
- The dashboard is plain HTML/CSS/JavaScript and polls the server every 3 seconds.
- Uploaded files are validated but stored unencrypted on disk.


---

## Author

Neville Kabamba – ST10404539 – PROG7312 (Parts 1–3)
