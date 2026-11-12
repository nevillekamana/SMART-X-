using System.Diagnostics;
using SmartX.Models.Streaming;

namespace SmartX.Services.Mesh;

/// <summary>Measured (not claimed) comparisons that the README and the dashboard quote. Everything here runs on private data — it never touches live telemetry.</summary>
public static class MeshBenchmarks
{
    /// <summary>A deliberately naive, unbalanced BST — only used to show what happens to sorted input without rotations.</summary>
    private sealed class PlainBst
    {
        private sealed class N { public long Key; public N? L, R; }
        private N? _root;
        public void Insert(long key)
        {
            var n = new N { Key = key };
            if (_root is null) { _root = n; return; }
            var cur = _root;
            while (true)
            {
                if (key < cur.Key) { if (cur.L is null) { cur.L = n; return; } cur = cur.L; }
                else { if (cur.R is null) { cur.R = n; return; } cur = cur.R; }
            }
        }
        public int Height()
        {
            if (_root is null) return 0;
            int best = 0;
            var stack = new Stack<(N Node, int Depth)>();
            stack.Push((_root, 1));
            while (stack.Count > 0)
            {
                var (n, d) = stack.Pop();
                best = Math.Max(best, d);
                if (n.L is not null) stack.Push((n.L, d + 1));
                if (n.R is not null) stack.Push((n.R, d + 1));
            }
            return best;
        }
    }

    private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 3);

    // ═══ AVL vs linear scan ═════════════════════════════════════════
    public static object Tree(int points, int queries)
    {
        points = Math.Clamp(points, 1_000, 500_000);
        queries = Math.Clamp(queries, 1, 5_000);
        var rnd = new Random(2026);

        // one reading every 5 s (with jitter), already in time order — the worst case for an unbalanced BST
        var list = new List<ReadingPoint>(points);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = t0;
        for (int i = 0; i < points; i++)
        {
            t = t.AddMilliseconds(4000 + rnd.Next(0, 2000));
            list.Add(new ReadingPoint(t, Math.Round(20 + 10 * Math.Sin(i / 500.0) + rnd.NextDouble(), 2), -60));
        }

        var swBuild = Stopwatch.StartNew();
        var tree = new AvlTree<long, ReadingPoint>();
        foreach (var p in list) tree.Insert(p.Timestamp.Ticks, p);
        swBuild.Stop();

        // random 2-hour windows
        var span = (list[^1].Timestamp - t0).TotalSeconds;
        var windows = new List<(long From, long To)>();
        for (int q = 0; q < queries; q++)
        {
            var start = t0.AddSeconds(rnd.NextDouble() * Math.Max(1, span - 7200));
            windows.Add((start.Ticks, start.AddHours(2).Ticks));
        }

        long totalMatchesAvl = 0, totalVisited = 0;
        var avlResults = new List<int>(queries);
        var swAvl = Stopwatch.StartNew();
        foreach (var (from, to) in windows)
        {
            int count = 0;
            totalVisited += tree.VisitRange(from, to, (_, _) => count++);
            avlResults.Add(count); totalMatchesAvl += count;
        }
        swAvl.Stop();

        long totalMatchesList = 0;
        var listResults = new List<int>(queries);
        var swList = Stopwatch.StartNew();
        foreach (var (from, to) in windows)
        {
            int count = 0;
            foreach (var p in list) { long k = p.Timestamp.Ticks; if (k >= from && k <= to) count++; }   // touches every reading
            listResults.Add(count); totalMatchesList += count;
        }
        swList.Stop();

        // identical results? compare per-window counts AND the actual readings of a sample window
        bool identical = avlResults.SequenceEqual(listResults);
        if (identical)
        {
            var (f, tt) = windows[0];
            var a = new List<ReadingPoint>(); tree.VisitRange(f, tt, (_, v) => a.Add(v));
            var b = list.Where(p => p.Timestamp.Ticks >= f && p.Timestamp.Ticks <= tt).ToList();
            identical = a.SequenceEqual(b);
        }

        // degeneration demo: 3,000 sorted inserts
        const int demo = 3000;
        var bst = new PlainBst(); var avl = new AvlTree<long, int>();
        for (int i = 0; i < demo; i++) { bst.Insert(i); avl.Insert(i, i); }

        return new
        {
            points, queries, windowHours = 2,
            avl = new
            {
                buildMs = Ms(swBuild), totalMs = Ms(swAvl), msPerQuery = Math.Round(swAvl.Elapsed.TotalMilliseconds / queries, 4),
                height = tree.Height, optimalHeight = TelemetryHistoryIndex.OptimalHeight(points), rotations = tree.Rotations,
                valid = tree.IsValid(), avgNodesVisited = Math.Round((double)totalVisited / queries, 1)
            },
            linearScan = new { totalMs = Ms(swList), msPerQuery = Math.Round(swList.Elapsed.TotalMilliseconds / queries, 4), readingsExaminedPerQuery = points },
            speedup = swAvl.Elapsed.TotalMilliseconds <= 0 ? 0 : Math.Round(swList.Elapsed.TotalMilliseconds / swAvl.Elapsed.TotalMilliseconds, 1),
            avgMatchesPerQuery = Math.Round((double)totalMatchesAvl / queries, 1),
            identical,
            degeneration = new
            {
                sortedInserts = demo, plainBstHeight = bst.Height(), avlHeight = avl.Height, avlOptimalHeight = TelemetryHistoryIndex.OptimalHeight(demo),
                note = "A plain BST fed already-sorted keys becomes a linked list (height = n). The AVL tree rotates to stay at about log2(n)."
            }
        };
    }

    // ═══ Graph algorithms on a random mesh ══════════════════════════
    public static object Graph(int nodes)
    {
        nodes = Math.Clamp(nodes, 10, 20_000);
        var rnd = new Random(7312);
        var g = new MeshGraph();
        for (int i = 0; i < nodes; i++) g.AddVertex($"N{i}");

        int id = 0;
        int Rssi() => -41 - rnd.Next(0, 55);
        for (int i = 1; i < nodes; i++)                                  // random spanning tree ⇒ always connected
        { int r = Rssi(); g.AddEdge(new MeshEdge($"E{id++}", $"N{i}", $"N{rnd.Next(0, i)}", MeshGraph.PowerCost(r), r)); }
        int extra = nodes * 2;                                           // + extra links ⇒ average degree ≈ 6
        for (int k = 0; k < extra; k++)
        { int a = rnd.Next(nodes), b = rnd.Next(nodes); int r = Rssi(); g.AddEdge(new MeshEdge($"E{id++}", $"N{a}", $"N{b}", MeshGraph.PowerCost(r), r)); }

        var sw = Stopwatch.StartNew(); var prim = g.Prim("N0"); sw.Stop(); double primMs = Ms(sw);
        sw.Restart(); var kru = g.Kruskal(); sw.Stop(); double kruMs = Ms(sw);
        sw.Restart(); var bfs = g.Bfs("N0"); sw.Stop(); double bfsMs = Ms(sw);
        sw.Restart(); var dfs = g.Dfs("N0"); sw.Stop(); double dfsMs = Ms(sw);
        sw.Restart(); var dij = g.Dijkstra("N0"); sw.Stop(); double dijMs = Ms(sw);
        sw.Restart(); var ap = g.ArticulationPoints("N0"); sw.Stop(); double apMs = Ms(sw);

        return new
        {
            nodes, edges = g.EdgeCount,
            prim = new { ms = primMs, cost = prim.TotalCost, edges = prim.Edges.Count },
            kruskal = new { ms = kruMs, cost = kru.TotalCost, edges = kru.Edges.Count },
            bfs = new { ms = bfsMs, reached = bfs.Hops.Count, maxHops = bfs.Hops.Values.Max() },
            dfs = new { ms = dfsMs, reached = dfs.Count },
            dijkstra = new { ms = dijMs, reached = dij.Dist.Count },
            articulationPoints = new { ms = apMs, count = ap.Count },
            costsAgree = Math.Abs(prim.TotalCost - kru.TotalCost) < 1e-9,
            spanningTreeEdges = prim.Edges.Count == nodes - 1 && kru.Edges.Count == nodes - 1
        };
    }
}
