using SmartX.Models.Streaming;
using SmartX.Services.Streaming;

namespace SmartX.Services.Mesh;

public record NodeRef(string Id, string? Mac, string Name, bool IsGateway, bool InGraph);

/// <summary>
/// The live mesh: owns the <see cref="MeshGraph"/>, the operator's simulated outages, and turns them (plus Part 2's
/// "Disconnected" alerts) into topology, connectivity and routing reports.
///
/// A node is DOWN when the operator simulated an outage OR Part 2 has an active Disconnected alert for it.
/// Down nodes are passed to every graph algorithm as the <c>blocked</c> set, so the MST, BFS levels and routes
/// are recomputed around them — the "self-healing" the brief describes.
/// </summary>
public class MeshNetwork
{
    public const string GatewayId = "GATEWAY";

    private sealed record NodeSeed(string Mac, string Name, string Kind, string Category, string Cluster, double X, double Y);
    private sealed class NodeInfo
    {
        public string Id = ""; public string? Mac; public string Name = ""; public string Kind = ""; public string Category = "";
        public string Cluster = ""; public double X, Y; public bool IsGateway;
    }

    public record ClusterBox(string Id, string Label, double X, double Y, double W, double H);

    // ── Seed layout: 13 demo devices + the gateway, in four clusters ─────────────────────────────
    private static readonly NodeSeed[] Seeds =
    {
        new("A4:CF:12:00:00:01", "Moisture Sensor A", "Sensor", "Environmental",    "bay1",       120, 110),
        new("A4:CF:12:00:00:09", "Pump B",            "Pump",   "Actuator",         "bay1",       250,  70),
        new("A4:CF:12:00:00:0B", "Valve 1",           "Valve",  "Actuator",         "bay1",       260, 190),
        new("A4:CF:12:00:00:02", "Moisture Sensor B", "Sensor", "Environmental",    "bay2",       110, 430),
        new("A4:CF:12:00:00:08", "Pump A",            "Pump",   "Actuator",         "bay2",       250, 490),
        new("A4:CF:12:00:00:0C", "Valve 2",           "Valve",  "Actuator",         "bay2",       260, 370),
        new("A4:CF:12:00:00:0D", "Fan 1",             "Fan",    "Actuator",         "greenhouse", 640, 150),
        new("A4:CF:12:00:00:03", "Greenhouse Temp 1", "Sensor", "Environmental",    "greenhouse", 780,  80),
        new("A4:CF:12:00:00:04", "Greenhouse Temp 2", "Sensor", "Environmental",    "greenhouse", 800, 200),
        new("A4:CF:12:00:00:05", "Smart Meter 1",     "Meter",  "PowerConsumption", "power",      640, 400),
        new("A4:CF:12:00:00:06", "Smart Meter 2",     "Meter",  "PowerConsumption", "power",      790, 440),
        new("A4:CF:12:00:00:07", "Smart Meter 3",     "Meter",  "PowerConsumption", "power",      700, 510),
        new("A4:CF:12:00:00:0A", "Relay 3",           "Relay",  "Actuator",         "power",      560, 460),
    };

    public static readonly ClusterBox[] Clusters =
    {
        new("bay1", "Hydroponics bay 1", 60, 30, 270, 200),
        new("bay2", "Hydroponics bay 2", 60, 330, 270, 200),
        new("greenhouse", "Greenhouse", 580, 30, 290, 200),
        new("power", "Power room", 520, 340, 350, 200),
    };

    // 22 links. Every RSSI is DISTINCT (and below −40 dBm), so every link weight is distinct, so the MST is unique
    // and Prim and Kruskal must return exactly the same tree. 13 links form the intended MST; 9 are weaker "spare" links.
    // Fan 1 is the ONLY way out of the greenhouse: both temperature sensors link to Fan 1 and to each other, nothing else.
    private static readonly (string A, string B, int Rssi)[] SeedLinks =
    {
        // gateway links
        (GatewayId, "A4:CF:12:00:00:0B", -52), (GatewayId, "A4:CF:12:00:00:0C", -54), (GatewayId, "A4:CF:12:00:00:0D", -50),
        (GatewayId, "A4:CF:12:00:00:05", -53), (GatewayId, "A4:CF:12:00:00:0A", -66),
        // bay 1
        ("A4:CF:12:00:00:0B", "A4:CF:12:00:00:01", -55), ("A4:CF:12:00:00:0B", "A4:CF:12:00:00:09", -58), ("A4:CF:12:00:00:01", "A4:CF:12:00:00:09", -67),
        ("A4:CF:12:00:00:09", "A4:CF:12:00:00:0D", -79),
        // bay 2
        ("A4:CF:12:00:00:0C", "A4:CF:12:00:00:02", -57), ("A4:CF:12:00:00:0C", "A4:CF:12:00:00:08", -60), ("A4:CF:12:00:00:02", "A4:CF:12:00:00:08", -70),
        ("A4:CF:12:00:00:08", "A4:CF:12:00:00:0A", -77),
        // greenhouse (Fan 1 is the single point of failure)
        ("A4:CF:12:00:00:0D", "A4:CF:12:00:00:03", -56), ("A4:CF:12:00:00:0D", "A4:CF:12:00:00:04", -59), ("A4:CF:12:00:00:03", "A4:CF:12:00:00:04", -72),
        // power room
        ("A4:CF:12:00:00:05", "A4:CF:12:00:00:06", -61), ("A4:CF:12:00:00:05", "A4:CF:12:00:00:0A", -62), ("A4:CF:12:00:00:0A", "A4:CF:12:00:00:07", -64),
        ("A4:CF:12:00:00:06", "A4:CF:12:00:00:07", -69),
        // cross-cluster spares
        ("A4:CF:12:00:00:0B", "A4:CF:12:00:00:0C", -74), ("A4:CF:12:00:00:05", "A4:CF:12:00:00:0D", -73),
    };

    private readonly object _gate = new();
    private readonly MeshGraph _graph = new();
    private readonly Dictionary<string, NodeInfo> _info = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _simulatedDown = new(StringComparer.OrdinalIgnoreCase);
    private int _nextLinkNumber;

    private readonly TelemetryHistoryIndex _history;
    private readonly DeviceRegistry _registry;
    private readonly IngestionPipeline _pipeline;
    private readonly TelemetryService _telemetry;

    public MeshNetwork(TelemetryHistoryIndex history, DeviceRegistry registry, IngestionPipeline pipeline, TelemetryService telemetry)
    {
        _history = history; _registry = registry; _pipeline = pipeline; _telemetry = telemetry;

        _graph.AddVertex(GatewayId);
        _info[GatewayId] = new NodeInfo { Id = GatewayId, Name = "Gateway", Kind = "Gateway", Category = "Gateway", Cluster = "gateway", X = 450, Y = 280, IsGateway = true };
        foreach (var s in Seeds)
        {
            _graph.AddVertex(s.Mac);
            _info[s.Mac] = new NodeInfo { Id = s.Mac, Mac = s.Mac, Name = s.Name, Kind = s.Kind, Category = s.Category, Cluster = s.Cluster, X = s.X, Y = s.Y };
        }
        foreach (var (a, b, rssi) in SeedLinks)
            _graph.AddEdge(new MeshEdge($"L{++_nextLinkNumber:00}", a, b, MeshGraph.PowerCost(rssi), rssi));
    }

    // ═══ Node resolution (used by EVERY endpoint) ═══════════════════
    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Accepts a MAC, a Part 1 sensor id, a device name ("Valve 1" / "valve1") or "gateway".</summary>
    public NodeRef? ResolveNode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = Uri.UnescapeDataString(token).Trim();
        _registry.SyncFrom(_telemetry);                              // pick up sensors registered through Part 1
        var nt = Norm(t);

        lock (_gate)
        {
            if (t.Equals("gateway", StringComparison.OrdinalIgnoreCase)) return Ref(_info[GatewayId]);
            if (_info.TryGetValue(t, out var byMac)) return Ref(byMac);            // MAC of a linked node
            foreach (var i in _info.Values) if (Norm(i.Name) == nt) return Ref(i);   // device name of a linked node
        }

        // Part 1 sensor id → MAC (O(1) in TelemetryService), or a registered device that is not linked yet
        var sensor = _telemetry.GetSensor(t);
        var mac = sensor?.MacAddress ?? t;
        lock (_gate) if (_info.TryGetValue(mac, out var linked)) return Ref(linked);

        foreach (var d in _registry.Snapshot())
            if (d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase) || d.Id.Equals(t, StringComparison.OrdinalIgnoreCase) || Norm(d.Name) == nt)
                return new NodeRef(d.Mac, d.Mac, d.Name, false, false);
        return null;
    }

    private static NodeRef Ref(NodeInfo i) => new(i.Id, i.Mac, i.Name, i.IsGateway, true);

    private string NameOf(string id)
    {
        lock (_gate)
        {
            if (_info.TryGetValue(id, out var i)) return i.Name;
        }
        return id;
    }

    // ═══ Down set ═══════════════════════════════════════════════════
    private (HashSet<string> Blocked, HashSet<string> Simulated, HashSet<string> Alerts) DownSets()
    {
        var alerts = _pipeline.GetDisconnectedMacs();
        lock (_gate)
        {
            var sim = new HashSet<string>(_simulatedDown, StringComparer.OrdinalIgnoreCase);
            var al = new HashSet<string>(alerts.Where(m => _info.ContainsKey(m) && !m.Equals(GatewayId, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var blocked = new HashSet<string>(sim, StringComparer.OrdinalIgnoreCase);
            blocked.UnionWith(al);
            return (blocked, sim, al);
        }
    }

    private static string Reason(string id, HashSet<string> sim, HashSet<string> alerts)
        => sim.Contains(id) && alerts.Contains(id) ? "simulated outage + Part 2 disconnect alert"
         : sim.Contains(id) ? "simulated outage" : alerts.Contains(id) ? "Part 2 disconnect alert" : "";

    // ═══ Routing state ══════════════════════════════════════════════
    private sealed class Routing
    {
        public HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase);
        public BfsResult Bfs = null!;
        public List<MeshEdge> TreeEdges = new();
        public double TreeCost;
        public Dictionary<string, string> NextHop = new(StringComparer.OrdinalIgnoreCase);
    }

    private static double SumCost(IEnumerable<MeshEdge> edges) => (double)Math.Round(edges.Sum(e => (decimal)e.Weight), 2);

    private Routing Route(string algorithm, HashSet<string> blocked)
    {
        var r = new Routing { Blocked = blocked, Bfs = _graph.Bfs(GatewayId, blocked) };
        if (algorithm == "kruskal")
        {
            // Kruskal returns a FOREST when the mesh is split; keep only the trees that touch the gateway.
            r.TreeEdges = _graph.Kruskal(blocked).Edges.Where(e => r.Bfs.Hops.ContainsKey(e.A)).ToList();
        }
        else r.TreeEdges = _graph.Prim(GatewayId, blocked).Edges;
        r.TreeCost = SumCost(r.TreeEdges);
        r.NextHop = MeshGraph.RootTree(r.TreeEdges, GatewayId);
        return r;
    }

    private static List<string> Names(MeshNetwork m, IEnumerable<string> ids) => ids.Select(m.NameOf).ToList();

    // ═══ GET /topology ══════════════════════════════════════════════
    public object GetTopology(string algorithm)
    {
        _registry.SyncFrom(_telemetry);
        var (blocked, sim, alerts) = DownSets();
        var devices = _registry.Snapshot();

        lock (_gate)
        {
            var cur = Route(algorithm, blocked);
            var prim = algorithm == "prim" ? cur : Route("prim", blocked);
            var kru = algorithm == "kruskal" ? cur : Route("kruskal", blocked);
            var spof = _graph.ArticulationPoints(GatewayId, blocked);
            var treeIds = cur.TreeEdges.Select(e => e.Id).ToHashSet();

            var nodes = new List<object>();
            int upCount = 0, isolatedCount = 0;
            foreach (var i in _info.Values.OrderBy(x => x.IsGateway ? 0 : 1).ThenBy(x => x.Cluster).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                string status = i.IsGateway ? "Gateway" : blocked.Contains(i.Id) ? "Down" : cur.Bfs.Hops.ContainsKey(i.Id) ? "Up" : "Isolated";
                if (status == "Up") upCount++; else if (status == "Isolated") isolatedCount++;
                cur.NextHop.TryGetValue(i.Id, out var next);
                List<string>? path = status is "Up" ? MeshGraph.PathToRoot(cur.NextHop, i.Id, GatewayId) : null;
                var dev = devices.FirstOrDefault(d => d.Mac.Equals(i.Id, StringComparison.OrdinalIgnoreCase));
                nodes.Add(new
                {
                    id = i.Id, mac = i.Mac, name = i.Name, kind = dev?.Kind ?? i.Kind, category = dev?.Category ?? i.Category,
                    cluster = i.Cluster, x = i.X, y = i.Y, status,
                    downReason = Reason(i.Id, sim, alerts),
                    hops = cur.Bfs.Hops.TryGetValue(i.Id, out var h) ? (int?)h : null,
                    nextHop = next, nextHopName = next is null ? null : NameOf(next),
                    routeCost = path is null ? (double?)null : _graph.PathCost(path),
                    singlePointOfFailure = spof.Contains(i.Id),
                    degree = _graph.Neighbours(i.Id).Count
                });
            }

            // registered Part 1 sensors that no link mentions yet
            int slot = 0;
            foreach (var d in devices.Where(d => !_info.ContainsKey(d.Mac)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(new
                {
                    id = d.Mac, mac = (string?)d.Mac, name = d.Name, kind = d.Kind, category = d.Category, cluster = "unlinked",
                    x = 60.0 + 110 * (slot % 7), y = 565.0, status = "Unlinked", downReason = "",
                    hops = (int?)null, nextHop = (string?)null, nextHopName = (string?)null, routeCost = (double?)null,
                    singlePointOfFailure = false, degree = 0
                });
                slot++;
            }

            var links = _graph.Edges.Select(e => new
            {
                id = e.Id, a = e.A, b = e.B, aName = NameOf(e.A), bName = NameOf(e.B), rssi = e.Rssi, weight = e.Weight,
                inTree = treeIds.Contains(e.Id), live = !blocked.Contains(e.A) && !blocked.Contains(e.B)
            }).ToList();

            var reach = cur.Bfs.Hops;
            double allLive = SumCost(_graph.Edges.Where(e => reach.ContainsKey(e.A) && reach.ContainsKey(e.B)));
            var far = reach.Where(kv => kv.Key != GatewayId).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            int total = _info.Count - 1;
            return new
            {
                algorithm,
                clusters = Clusters,
                nodes, links,
                tree = new
                {
                    algorithm, edgeIds = treeIds.OrderBy(x => x).ToList(), edgeCount = cur.TreeEdges.Count, cost = cur.TreeCost,
                    primCost = prim.TreeCost, kruskalCost = kru.TreeCost,
                    agree = Math.Abs(prim.TreeCost - kru.TreeCost) < 1e-9
                        && prim.TreeEdges.Select(e => e.Id).OrderBy(x => x).SequenceEqual(kru.TreeEdges.Select(e => e.Id).OrderBy(x => x))
                },
                kpis = new
                {
                    online = upCount,
                    offline = blocked.Count,
                    cutOff = isolatedCount,
                    unlinked = devices.Count(d => !_info.ContainsKey(d.Mac)),
                    totalLinked = total,
                    treeCost = cur.TreeCost,
                    allLinksCost = allLive,
                    powerSaved = Math.Round(allLive - cur.TreeCost, 2),
                    powerSavedPct = allLive <= 0 ? 0 : Math.Round(100 * (allLive - cur.TreeCost) / allLive, 1),
                    longestPathHops = far.Key is null ? 0 : far.Value,
                    longestPathNode = far.Key is null ? null : NameOf(far.Key)
                }
            };
        }
    }

    // ═══ GET /connectivity ══════════════════════════════════════════
    public object GetConnectivity()
    {
        var (blocked, sim, alerts) = DownSets();
        lock (_gate)
        {
            var cur = Route("prim", blocked);
            var baseline = Route("prim", new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // BFS levels: who is how many hops from the gateway right now
            var levels = cur.Bfs.Hops.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => new { level = g.Key, nodes = g.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new { id = kv.Key, name = NameOf(kv.Key) }).ToList() }).ToList();

            // Down nodes: which neighbours used them as the next hop, and where those neighbours send traffic now
            var downNodes = blocked.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase).Select(d => new
            {
                id = d, name = NameOf(d), reason = Reason(d, sim, alerts),
                affected = baseline.NextHop.Where(kv => kv.Value.Equals(d, StringComparison.OrdinalIgnoreCase) && !blocked.Contains(kv.Key))
                    .OrderBy(kv => NameOf(kv.Key), StringComparer.OrdinalIgnoreCase)
                    .Select(kv =>
                    {
                        cur.NextHop.TryGetValue(kv.Key, out var now);
                        return new
                        {
                            id = kv.Key, name = NameOf(kv.Key),
                            oldNextHop = NameOf(kv.Value), newNextHop = now is null ? null : NameOf(now),
                            status = now is null ? "isolated" : "rerouted"
                        };
                    }).ToList()
            }).ToList();

            // Isolated: alive, but every path to the gateway crosses a down node → suggest the nearest live node as a relay
            var live = _info.Values.Where(i => !i.IsGateway && !blocked.Contains(i.Id)).ToList();
            var isolated = live.Where(i => !cur.Bfs.Hops.ContainsKey(i.Id)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i =>
            {
                var relay = _info.Values.Where(o => cur.Bfs.Hops.ContainsKey(o.Id))
                    .OrderBy(o => Math.Sqrt((o.X - i.X) * (o.X - i.X) + (o.Y - i.Y) * (o.Y - i.Y))).FirstOrDefault();
                return new
                {
                    id = i.Id, name = i.Name,
                    suggestedRelay = relay is null ? null : new { id = relay.Id, name = relay.Name, distance = Math.Round(Math.Sqrt((relay.X - i.X) * (relay.X - i.X) + (relay.Y - i.Y) * (relay.Y - i.Y))) },
                    suggestion = relay is null ? "No live node in range." : $"Place a relay or re-aim an antenna toward {relay.Name} (nearest live node)."
                };
            }).ToList();

            // DFS sub-networks of the live mesh
            var comps = _graph.ConnectedComponents(blocked);
            var subNetworks = comps.Select(c => new
            {
                size = c.Count, containsGateway = c.Contains(GatewayId, StringComparer.OrdinalIgnoreCase),
                nodes = c.Select(id => new { id, name = NameOf(id) }).ToList()
            }).OrderByDescending(c => c.containsGateway).ThenByDescending(c => c.size).ToList();

            // Single points of failure: what would be cut off if each articulation point died right now
            var spof = _graph.ArticulationPoints(GatewayId, blocked).OrderBy(NameOf, StringComparer.OrdinalIgnoreCase).Select(p =>
            {
                var after = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase) { p };
                var still = _graph.Bfs(GatewayId, after).Hops.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var lost = cur.Bfs.Hops.Keys.Where(k => !still.Contains(k) && !k.Equals(p, StringComparison.OrdinalIgnoreCase)).OrderBy(NameOf, StringComparer.OrdinalIgnoreCase).ToList();
                return new { id = p, name = NameOf(p), wouldCutOff = lost.Select(id => new { id, name = NameOf(id) }).ToList() };
            }).ToList();

            return new
            {
                summary = new
                {
                    reachable = cur.Bfs.Hops.Count - 1, down = blocked.Count, isolated = isolated.Count,
                    subNetworks = subNetworks.Count, singlePointsOfFailure = spof.Count,
                    healthy = blocked.Count == 0
                },
                levels, downNodes, isolated, subNetworks, singlePointsOfFailure = spof
            };
        }
    }

    // ═══ GET /routes/{id} ═══════════════════════════════════════════
    private object PathInfo(List<string>? path)
        => path is null ? new { found = false, path = new List<string>(), names = new List<string>(), hops = 0, cost = (double?)null }
                        : new { found = true, path, names = path.Select(NameOf).ToList(), hops = path.Count - 1, cost = (double?)_graph.PathCost(path) };

    public object GetRoute(NodeRef node)
    {
        var (blocked, sim, alerts) = DownSets();
        lock (_gate)
        {
            if (!node.InGraph)
                return new
                {
                    node = new { id = node.Id, name = node.Name, mac = node.Mac }, status = "Unlinked", reachable = false, rerouted = false,
                    mst = PathInfo(null), dijkstra = PathInfo(null), bfs = PathInfo(null), preOutage = PathInfo(null), best = (string?)null,
                    failoverNote = $"{node.Name} is registered but has no wireless links yet. Add a link (POST /api/mesh/links) to bring it into the mesh."
                };

            var cur = Route("prim", blocked);
            var baseline = Route("prim", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string status = node.IsGateway ? "Gateway" : blocked.Contains(node.Id) ? "Down" : cur.Bfs.Hops.ContainsKey(node.Id) ? "Up" : "Isolated";

            List<string>? mst = null, dij = null, bfs = null, pre = null;
            if (node.IsGateway) { mst = dij = bfs = pre = new List<string> { GatewayId }; }
            else
            {
                pre = MeshGraph.PathToRoot(baseline.NextHop, node.Id, GatewayId);
                if (status == "Up")
                {
                    mst = MeshGraph.PathToRoot(cur.NextHop, node.Id, GatewayId);
                    dij = MeshGraph.DijkstraPath(_graph.Dijkstra(GatewayId, blocked), node.Id, GatewayId);
                    bfs = BfsPath(cur.Bfs, node.Id);
                }
            }

            double? cMst = mst is null ? null : _graph.PathCost(mst), cDij = dij is null ? null : _graph.PathCost(dij), cBfs = bfs is null ? null : _graph.PathCost(bfs);
            string? best = null;
            var costs = new (string Key, double? Cost)[] { ("mst", cMst), ("dijkstra", cDij), ("bfs", cBfs) }.Where(c => c.Cost.HasValue).ToList();
            if (costs.Count > 0) best = costs.OrderBy(c => c.Cost!.Value).First().Key;

            bool rerouted = status == "Up" && !node.IsGateway && pre is not null && mst is not null && !pre.SequenceEqual(mst, StringComparer.OrdinalIgnoreCase);
            string note = FailoverNote(node, status, blocked, sim, alerts, pre, mst, rerouted, cur);

            return new
            {
                node = new { id = node.Id, name = node.Name, mac = node.Mac }, status,
                reachable = status is "Up" or "Gateway", rerouted,
                mst = PathInfo(mst), dijkstra = PathInfo(dij), bfs = PathInfo(bfs), preOutage = PathInfo(pre),
                best, failoverNote = note
            };
        }
    }

    private static List<string>? BfsPath(BfsResult r, string node)
    {
        if (!r.Hops.ContainsKey(node)) return null;
        var path = new List<string>();
        string? cur = node;
        while (cur is not null) { path.Add(cur); cur = r.Parent.TryGetValue(cur, out var p) ? p : null; }
        return path;
    }

    private string FailoverNote(NodeRef node, string status, HashSet<string> blocked, HashSet<string> sim, HashSet<string> alerts,
        List<string>? pre, List<string>? now, bool rerouted, Routing cur)
    {
        if (node.IsGateway) return "This is the gateway — every route ends here.";
        if (status == "Down") return $"{node.Name} is DOWN ({Reason(node.Id, sim, alerts)}), so it carries no traffic.";
        if (status == "Isolated")
        {
            var me = _info[node.Id];
            var relay = _info.Values.Where(o => cur.Bfs.Hops.ContainsKey(o.Id)).OrderBy(o => Math.Sqrt((o.X - me.X) * (o.X - me.X) + (o.Y - me.Y) * (o.Y - me.Y))).FirstOrDefault();
            var culprits = blocked.Select(NameOf).OrderBy(x => x).ToList();
            return $"{node.Name} is CUT OFF: every path to the gateway crosses a down node ({string.Join(", ", culprits)})."
                 + (relay is null ? "" : $" Nearest live relay: {relay.Name}.");
        }
        if (rerouted && pre is not null && now is not null)
        {
            double oldCost = _graph.PathCost(pre), newCost = _graph.PathCost(now);
            double delta = Math.Round(newCost - oldCost, 2);
            string pct = oldCost > 0 ? $" ({(delta >= 0 ? "+" : "")}{Math.Round(100 * delta / oldCost)}%)" : "";
            int dh = (now.Count - 1) - (pre.Count - 1);
            var around = blocked.Select(NameOf).OrderBy(x => x).ToList();
            return $"Rerouted around {string.Join(", ", around)}: {string.Join(" → ", now.Select(NameOf))} instead of {string.Join(" → ", pre.Select(NameOf))}. "
                 + $"Energy {(delta >= 0 ? "+" : "")}{delta:0.##} power units{pct}, {(dh >= 0 ? "+" : "")}{dh} hop{(Math.Abs(dh) == 1 ? "" : "s")}.";
        }
        return blocked.Count == 0 ? "No outages: this is the normal minimum-spanning-tree route." : "Route unchanged by the current outages.";
    }

    // ═══ GET /nodes ═════════════════════════════════════════════════
    public object GetNodes()
    {
        _registry.SyncFrom(_telemetry);
        var (blocked, sim, alerts) = DownSets();
        var devices = _registry.Snapshot();
        lock (_gate)
        {
            var reach = _graph.Bfs(GatewayId, blocked).Hops;
            var rows = new List<object>();
            foreach (var i in _info.Values.OrderBy(x => x.IsGateway ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var dev = devices.FirstOrDefault(d => d.Mac.Equals(i.Id, StringComparison.OrdinalIgnoreCase));
                rows.Add(new
                {
                    id = i.Id, mac = i.Mac, name = i.Name, kind = dev?.Kind ?? i.Kind, category = dev?.Category ?? i.Category, cluster = i.Cluster,
                    linked = true, degree = _graph.Neighbours(i.Id).Count,
                    status = i.IsGateway ? "Gateway" : blocked.Contains(i.Id) ? "Down" : reach.ContainsKey(i.Id) ? "Up" : "Isolated",
                    downReason = Reason(i.Id, sim, alerts),
                    historyPoints = i.IsGateway ? 0 : _history.Count(i.Id)
                });
            }
            foreach (var d in devices.Where(d => !_info.ContainsKey(d.Mac)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                rows.Add(new { id = d.Mac, mac = (string?)d.Mac, name = d.Name, kind = d.Kind, category = d.Category, cluster = "unlinked", linked = false, degree = 0, status = "Unlinked", downReason = "", historyPoints = _history.Count(d.Mac) });
            return rows;
        }
    }

    // ═══ Operator actions ═══════════════════════════════════════════
    /// <exception cref="ArgumentException">gateway (400)</exception>
    /// <exception cref="InvalidOperationException">node not linked into the mesh (409)</exception>
    public object Fail(NodeRef node)
    {
        if (node.IsGateway) throw new ArgumentException("The gateway cannot be failed: it is the root every route ends at.");
        if (!node.InGraph) throw new InvalidOperationException($"{node.Name} has no links yet — link it into the mesh before simulating an outage.");
        bool added;
        lock (_gate) added = _simulatedDown.Add(node.Id);
        return new { node = node.Name, id = node.Id, status = "Down", alreadyDown = !added };
    }

    /// <exception cref="InvalidOperationException">node is already up, or is down only because of a Part 2 alert (409)</exception>
    public object Recover(NodeRef node)
    {
        if (node.IsGateway || !node.InGraph) throw new InvalidOperationException($"{node.Name} is not a failed mesh node.");
        var (_, sim, alerts) = DownSets();
        if (!sim.Contains(node.Id))
        {
            throw new InvalidOperationException(alerts.Contains(node.Id)
                ? $"{node.Name} is down because of an active Part 2 Disconnected alert, not a simulated outage. It recovers automatically when the device sends telemetry again."
                : $"{node.Name} is already up.");
        }
        lock (_gate) _simulatedDown.Remove(node.Id);
        bool stillDown = alerts.Contains(node.Id);
        return new { node = node.Name, id = node.Id, status = stillDown ? "Down" : "Up", stillDownBecause = stillDown ? "Part 2 disconnect alert" : null };
    }

    public object ResetOutages()
    {
        int n;
        lock (_gate) { n = _simulatedDown.Count; _simulatedDown.Clear(); }
        return new { cleared = n };
    }

    /// <exception cref="ArgumentException">RSSI outside −100..−30 (400)</exception>
    /// <exception cref="KeyNotFoundException">unknown node (404)</exception>
    /// <exception cref="InvalidOperationException">self link or duplicate link (409)</exception>
    public object AddLink(NodeRef? a, NodeRef? b, int rssi)
    {
        if (rssi < -100 || rssi > -30) throw new ArgumentException("RSSI must be between -100 and -30 dBm.");
        if (a is null || b is null) throw new KeyNotFoundException("Unknown node: use a MAC, sensor id, device name or 'gateway'.");
        if (a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A node cannot link to itself.");

        lock (_gate)
        {
            if (_graph.HasEdge(a.Id, b.Id)) throw new InvalidOperationException($"{a.Name} and {b.Name} are already linked.");
            foreach (var (n, peer) in new[] { (a, b), (b, a) })
                if (!_info.ContainsKey(n.Id)) Admit(n, _info[peer.Id]);

            var edge = new MeshEdge($"L{++_nextLinkNumber:00}", _info[a.Id].Id, _info[b.Id].Id, MeshGraph.PowerCost(rssi), rssi);
            _graph.AddEdge(edge);
            return new { link = new { id = edge.Id, a = edge.A, b = edge.B, aName = a.Name, bName = b.Name, rssi, weight = edge.Weight } };
        }
    }

    /// <summary>First link for a registered-but-unlinked device: it becomes a vertex, placed beside the node it connects to.</summary>
    private void Admit(NodeRef n, NodeInfo peer)
    {
        double x = Math.Clamp(peer.X + 70, 30, 870), y = Math.Clamp(peer.Y + 60, 30, 540);
        while (_info.Values.Any(o => Math.Abs(o.X - x) < 45 && Math.Abs(o.Y - y) < 35)) { x += 25; y += 20; if (x > 870) x = 40; if (y > 540) y = 40; }
        var dev = _registry.Snapshot().FirstOrDefault(d => d.Mac.Equals(n.Id, StringComparison.OrdinalIgnoreCase));
        _graph.AddVertex(n.Id);
        _info[n.Id] = new NodeInfo { Id = n.Id, Mac = n.Mac, Name = n.Name, Kind = dev?.Kind ?? "Sensor", Category = dev?.Category ?? "Environmental", Cluster = peer.Cluster == "gateway" ? "power" : peer.Cluster, X = x, Y = y };
    }

    // ═══ GET /nodes/{id}/history ════════════════════════════════════
    public HistoryQueryResult History(NodeRef node, DateTime? from, DateTime? to, int page, int pageSize)
    {
        var meta = _registry.TryGetView(node.Mac ?? node.Id, out var v) ? v : null;
        var r = _history.Query(node.Mac ?? node.Id, from, to, page, pageSize, meta);
        r.Name = node.Name;
        return r;
    }
}
