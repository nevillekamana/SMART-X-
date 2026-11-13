namespace SmartX.Services.Mesh;

/// <summary>One wireless link between two nodes. Weight is the transmit-power cost derived from the RSSI.</summary>
public record MeshEdge(string Id, string A, string B, double Weight, int Rssi)
{
    public string Other(string v) => v.Equals(A, StringComparison.OrdinalIgnoreCase) ? B : A;
}

public record BfsResult(Dictionary<string, int> Hops, Dictionary<string, string?> Parent, List<string> Order);
public record MstResult(List<MeshEdge> Edges, double TotalCost, int ComponentsTouched);
public record DijkstraResult(Dictionary<string, double> Dist, Dictionary<string, string> Previous);

/// <summary>
/// The IoT mesh as an undirected weighted graph: vertices are ESP32 nodes (plus the gateway), edges are wireless links.
/// Stored as an ADJACENCY LIST — Dictionary&lt;vertex, List&lt;edge&gt;&gt; — so listing a node's neighbours costs O(degree)
/// instead of O(V) for a matrix, which matters because real meshes are sparse (each node hears only a few neighbours).
/// Vertex ids are case-insensitive. Every algorithm accepts an optional <c>blocked</c> set of "down" nodes that are
/// treated as removed from the graph — that is how a failed node is simulated without mutating the topology.
/// </summary>
public class MeshGraph
{
    private readonly Dictionary<string, List<MeshEdge>> _adj = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MeshEdge> _edges = new();
    private readonly HashSet<string> _pairs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MeshEdge> Edges => _edges;
    public IEnumerable<string> Vertices => _canonical.Values;
    public int VertexCount => _adj.Count;
    public int EdgeCount => _edges.Count;

    /// <summary>Transmit-power cost of a link: 1 at −40 dBm or better, rising quadratically as the signal weakens.</summary>
    public static double PowerCost(int rssi) => Math.Round(1 + Math.Pow(Math.Max(0, (-rssi - 40) / 10.0), 2), 2);

    private static string PairKey(string a, string b)
        => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    public string? Canonical(string id) => _canonical.TryGetValue(id, out var c) ? c : null;
    public bool HasVertex(string id) => _adj.ContainsKey(id);
    public bool HasEdge(string a, string b) => _pairs.Contains(PairKey(a, b));

    public void AddVertex(string id)
    {
        if (_adj.ContainsKey(id)) return;
        _adj[id] = new List<MeshEdge>();
        _canonical[id] = id;
    }

    /// <summary>Adds a link. Returns false for self-links, unknown vertices or an existing link between the pair.</summary>
    public bool AddEdge(MeshEdge e)
    {
        if (e.A.Equals(e.B, StringComparison.OrdinalIgnoreCase)) return false;
        if (!_adj.ContainsKey(e.A) || !_adj.ContainsKey(e.B)) return false;
        if (!_pairs.Add(PairKey(e.A, e.B))) return false;
        _edges.Add(e);
        _adj[e.A].Add(e);
        _adj[e.B].Add(e);
        return true;
    }

    public IReadOnlyList<MeshEdge> Neighbours(string v) => _adj.TryGetValue(v, out var l) ? l : Array.Empty<MeshEdge>();

    private static bool Down(HashSet<string>? blocked, string v) => blocked is not null && blocked.Contains(v);

    // ═══ BFS — Queue ════════════════════════════════════════════════
    /// <summary>
    /// Breadth-first search from <paramref name="source"/>. A Queue&lt;T&gt; explores the mesh level by level, so the first time
    /// a node is reached it is by the FEWEST hops. Returns hops-from-source and each node's parent (the way back).
    /// </summary>
    public BfsResult Bfs(string source, HashSet<string>? blocked = null)
    {
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        if (!_adj.ContainsKey(source) || Down(blocked, source)) return new BfsResult(hops, parent, order);

        var queue = new Queue<string>();
        string start = _canonical[source];
        hops[start] = 0; parent[start] = null; queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            order.Add(v);
            foreach (var e in _adj[v])
            {
                var w = e.Other(v);
                if (Down(blocked, w) || hops.ContainsKey(w)) continue;
                hops[w] = hops[v] + 1;
                parent[w] = v;
                queue.Enqueue(w);
            }
        }
        return new BfsResult(hops, parent, order);
    }

    // ═══ DFS — Stack (iterative) ════════════════════════════════════
    /// <summary>Depth-first visit order from <paramref name="source"/>, using an explicit Stack&lt;T&gt; (no recursion, so no stack overflow on deep meshes).</summary>
    public List<string> Dfs(string source, HashSet<string>? blocked = null)
    {
        var order = new List<string>();
        if (!_adj.ContainsKey(source) || Down(blocked, source)) return order;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(_canonical[source]);
        while (stack.Count > 0)
        {
            var v = stack.Pop();
            if (!seen.Add(v)) continue;
            order.Add(v);
            // push neighbours in reverse id order so the lowest id is explored first (deterministic output)
            foreach (var w in _adj[v].Select(e => e.Other(v)).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                if (!Down(blocked, w) && !seen.Contains(w)) stack.Push(w);
        }
        return order;
    }

    /// <summary>Every group of nodes that can still reach each other (the "sub-networks"), largest first.</summary>
    public List<List<string>> ConnectedComponents(HashSet<string>? blocked = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<List<string>>();
        foreach (var v in Vertices.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (Down(blocked, v) || seen.Contains(v)) continue;
            var comp = Dfs(v, blocked);
            foreach (var c in comp) seen.Add(c);
            result.Add(comp.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }
        return result.OrderByDescending(c => c.Count).ThenBy(c => c[0], StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═══ Articulation points — Tarjan ═══════════════════════════════
    /// <summary>
    /// Single points of failure: nodes whose removal disconnects part of the mesh. Tarjan's algorithm records, for each
    /// node, its DFS discovery time and the lowest discovery time reachable through its sub-tree (low-link).
    /// A non-root node v is an articulation point if some child c has low[c] ≥ disc[v]; the root is one if it has ≥ 2 DFS children.
    /// Iterative, O(V + E).
    /// </summary>
    public HashSet<string> ArticulationPoints(string root, HashSet<string>? blocked = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_adj.ContainsKey(root) || Down(blocked, root)) return result;

        var disc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int timer = 0, rootChildren = 0;

        var stack = new Stack<(string Node, int Next)>();
        string r = _canonical[root];
        disc[r] = low[r] = ++timer; parent[r] = null;
        stack.Push((r, 0));

        while (stack.Count > 0)
        {
            var (v, idx) = stack.Pop();
            var nbrs = _adj[v];
            if (idx < nbrs.Count)
            {
                stack.Push((v, idx + 1));                          // come back for the next neighbour later
                var w = nbrs[idx].Other(v);
                if (Down(blocked, w)) continue;
                if (!disc.ContainsKey(w))                          // tree edge: descend
                {
                    parent[w] = v;
                    disc[w] = low[w] = ++timer;
                    if (v.Equals(r, StringComparison.OrdinalIgnoreCase)) rootChildren++;
                    stack.Push((w, 0));
                }
                else if (!string.Equals(parent[v], w, StringComparison.OrdinalIgnoreCase))
                    low[v] = Math.Min(low[v], disc[w]);            // back edge
            }
            else if (parent[v] is string p)                        // v finished: fold its low-link into the parent
            {
                low[p] = Math.Min(low[p], low[v]);
                if (!p.Equals(r, StringComparison.OrdinalIgnoreCase) && low[v] >= disc[p]) result.Add(p);
            }
        }
        if (rootChildren >= 2) result.Add(r);
        return result;
    }

    // ═══ Prim — PriorityQueue ═══════════════════════════════════════
    private sealed class EdgeOrder : IComparer<(double, string)>
    {
        public int Compare((double, string) x, (double, string) y)
        {
            int c = x.Item1.CompareTo(y.Item1);
            return c != 0 ? c : string.CompareOrdinal(x.Item2, y.Item2);   // deterministic tie-break on edge id
        }
    }

    /// <summary>
    /// Prim's minimum spanning tree grown outward from <paramref name="root"/>. A min-heap (PriorityQueue) always offers the
    /// cheapest link that crosses from the tree to a node not yet in it. Priority = (weight, edge id) so equal weights
    /// resolve the same way every run. Only the root's component is spanned.
    /// </summary>
    public MstResult Prim(string root, HashSet<string>? blocked = null)
    {
        var edges = new List<MeshEdge>();
        if (!_adj.ContainsKey(root) || Down(blocked, root)) return new MstResult(edges, 0, 0);

        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pq = new PriorityQueue<(MeshEdge Edge, string To), (double, string)>(new EdgeOrder());
        string r = _canonical[root];
        inTree.Add(r);
        foreach (var e in _adj[r]) if (!Down(blocked, e.Other(r))) pq.Enqueue((e, e.Other(r)), (e.Weight, e.Id));

        decimal total = 0;      // decimal: weights have 2 dp, so the sum is exact whatever order the algorithm adds them in
        while (pq.Count > 0)
        {
            var (e, to) = pq.Dequeue();
            if (!inTree.Add(to)) continue;                         // that end is already connected: skip (would form a cycle)
            edges.Add(e);
            total += (decimal)e.Weight;
            foreach (var n in _adj[to])
            {
                var w = n.Other(to);
                if (!inTree.Contains(w) && !Down(blocked, w)) pq.Enqueue((n, w), (n.Weight, n.Id));
            }
        }
        return new MstResult(edges, (double)Math.Round(total, 2), 1);
    }

    // ═══ Kruskal — union-find ═══════════════════════════════════════
    /// <summary>Disjoint-set forest. Path compression flattens chains on Find; union by rank keeps trees shallow ⇒ near-O(1) per operation.</summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);
        public UnionFind(IEnumerable<string> items) { foreach (var i in items) { _parent[i] = i; _rank[i] = 0; } }

        public string Find(string x)
        {
            var root = x;
            while (!_parent[root].Equals(root, StringComparison.OrdinalIgnoreCase)) root = _parent[root];
            while (!_parent[x].Equals(root, StringComparison.OrdinalIgnoreCase))     // path compression
            {
                var next = _parent[x]; _parent[x] = root; x = next;
            }
            return root;
        }

        public bool Union(string a, string b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra.Equals(rb, StringComparison.OrdinalIgnoreCase)) return false;    // same set: this link would close a cycle
            if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);                        // union by rank
            _parent[rb] = ra;
            if (_rank[ra] == _rank[rb]) _rank[ra]++;
            return true;
        }
    }

    /// <summary>
    /// Kruskal's algorithm: sort all links by (weight, id), then take each link whose ends are still in different sets.
    /// On a graph that is not fully connected the result is a spanning FOREST (one tree per component) —
    /// the caller keeps only the trees it needs.
    /// </summary>
    public MstResult Kruskal(HashSet<string>? blocked = null)
    {
        var live = Vertices.Where(v => !Down(blocked, v)).ToList();
        var uf = new UnionFind(live);
        var sorted = _edges
            .Where(e => !Down(blocked, e.A) && !Down(blocked, e.B))
            .OrderBy(e => e.Weight).ThenBy(e => e.Id, StringComparer.Ordinal);

        var chosen = new List<MeshEdge>();
        decimal total = 0;
        foreach (var e in sorted)
            if (uf.Union(e.A, e.B)) { chosen.Add(e); total += (decimal)e.Weight; }

        return new MstResult(chosen, (double)Math.Round(total, 2), live.Count - chosen.Count);   // components = V − chosen edges
    }

    // ═══ Routing over a tree ════════════════════════════════════════
    /// <summary>
    /// Turns an undirected spanning tree into routing state: for every node, the NEXT HOP toward <paramref name="root"/>.
    /// This is what a mesh node actually stores — it doesn't know the whole path, only which neighbour to hand the packet to.
    /// </summary>
    public static Dictionary<string, string> RootTree(IEnumerable<MeshEdge> edges, string root)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            if (!adj.TryGetValue(e.A, out var la)) adj[e.A] = la = new List<string>();
            if (!adj.TryGetValue(e.B, out var lb)) adj[e.B] = lb = new List<string>();
            la.Add(e.B); lb.Add(e.A);
        }

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var q = new Queue<string>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var v = q.Dequeue();
            if (!adj.TryGetValue(v, out var nbrs)) continue;
            foreach (var w in nbrs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(w)) { next[w] = v; q.Enqueue(w); }
        }
        return next;
    }

    /// <summary>Follows next-hop pointers from <paramref name="node"/> to <paramref name="root"/>. Returns null if the node has no route.</summary>
    public static List<string>? PathToRoot(Dictionary<string, string> nextHop, string node, string root)
    {
        var path = new List<string> { node };
        var cur = node;
        int guard = nextHop.Count + 2;
        while (!cur.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (!nextHop.TryGetValue(cur, out var n) || guard-- <= 0) return null;
            path.Add(n);
            cur = n;
        }
        return path;
    }

    public double PathCost(IReadOnlyList<string> path)
    {
        decimal sum = 0;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var e = _adj[path[i]].FirstOrDefault(x => x.Other(path[i]).Equals(path[i + 1], StringComparison.OrdinalIgnoreCase));
            if (e is not null) sum += (decimal)e.Weight;
        }
        return (double)Math.Round(sum, 2);
    }

    // ═══ Dijkstra — baseline ════════════════════════════════════════
    /// <summary>
    /// Single-source shortest paths by total link cost. The MST minimises the cost of the WHOLE tree, not of each node's own
    /// route, so Dijkstra is the per-node baseline: it shows how much extra power the shared MST route costs (or saves) per node.
    /// </summary>
    public DijkstraResult Dijkstra(string source, HashSet<string>? blocked = null)
    {
        var dist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_adj.ContainsKey(source) || Down(blocked, source)) return new DijkstraResult(dist, prev);

        var pq = new PriorityQueue<string, (double, string)>(new EdgeOrder());
        string s = _canonical[source];
        dist[s] = 0; pq.Enqueue(s, (0, s));
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pq.Count > 0)
        {
            var v = pq.Dequeue();
            if (!done.Add(v)) continue;
            foreach (var e in _adj[v])
            {
                var w = e.Other(v);
                if (Down(blocked, w) || done.Contains(w)) continue;
                double nd = dist[v] + e.Weight;
                if (!dist.TryGetValue(w, out var old) || nd < old - 1e-9)
                {
                    dist[w] = nd; prev[w] = v;
                    pq.Enqueue(w, (nd, w));
                }
            }
        }
        return new DijkstraResult(dist, prev);
    }

    /// <summary>Path from <paramref name="node"/> back to the Dijkstra source (node first, source last), or null if unreachable.</summary>
    public static List<string>? DijkstraPath(DijkstraResult r, string node, string source)
    {
        if (!r.Dist.ContainsKey(node)) return null;
        var path = new List<string> { node };
        var cur = node;
        int guard = r.Dist.Count + 2;
        while (!cur.Equals(source, StringComparison.OrdinalIgnoreCase))
        {
            if (!r.Previous.TryGetValue(cur, out var p) || guard-- <= 0) return null;
            path.Add(p); cur = p;
        }
        return path;
    }
}
