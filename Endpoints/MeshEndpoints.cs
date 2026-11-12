using System.Globalization;
using SmartX.Services.Mesh;

namespace SmartX.Endpoints;

public record LinkRequest(string? A, string? B, int? Rssi);

/// <summary>All final-PoE routes live under /api/mesh. Every route that takes a node uses MeshNetwork.ResolveNode (MAC, sensor id, name or "gateway").</summary>
public static class MeshEndpoints
{
    private static readonly object NotFoundBody = new { error = "Unknown node. Use a MAC address, sensor id, device name or 'gateway'." };

    public static void MapMeshEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/mesh").WithTags("Part 3 — Network Topology & Mesh Routing");

        // ── Topology: graph + minimum spanning tree ─────────────────────────────
        g.MapGet("/topology", (string? algorithm, MeshNetwork m) =>
        {
            var alg = (algorithm ?? "prim").Trim().ToLowerInvariant();
            if (alg is not ("prim" or "kruskal")) return Results.BadRequest(new { error = "algorithm must be 'prim' or 'kruskal'." });
            return Results.Ok(m.GetTopology(alg));
        });

        g.MapGet("/connectivity", (MeshNetwork m) => Results.Ok(m.GetConnectivity()));

        g.MapGet("/routes/{id}", (string id, MeshNetwork m) =>
        {
            var node = m.ResolveNode(id);
            return node is null ? Results.NotFound(NotFoundBody) : Results.Ok(m.GetRoute(node));
        });

        g.MapGet("/nodes", (MeshNetwork m) => Results.Ok(m.GetNodes()));

        // ── Outage simulation ───────────────────────────────────────────────────
        g.MapPost("/nodes/{id}/fail", (string id, MeshNetwork m) =>
        {
            var node = m.ResolveNode(id);
            return node is null ? Results.NotFound(NotFoundBody) : Guard(() => Results.Ok(m.Fail(node)));
        });

        g.MapPost("/nodes/{id}/recover", (string id, MeshNetwork m) =>
        {
            var node = m.ResolveNode(id);
            return node is null ? Results.NotFound(NotFoundBody) : Guard(() => Results.Ok(m.Recover(node)));
        });

        g.MapPost("/outages/reset", (MeshNetwork m) => Results.Ok(m.ResetOutages()));

        // ── Add a link ──────────────────────────────────────────────────────────
        g.MapPost("/links", (LinkRequest req, MeshNetwork m) =>
        {
            if (req is null || req.Rssi is null) return Results.BadRequest(new { error = "Body must be {a, b, rssi}." });
            return Guard(() => Results.Ok(m.AddLink(m.ResolveNode(req.A), m.ResolveNode(req.B), req.Rssi.Value)));
        });

        // ── Telemetry history (AVL range query) ─────────────────────────────────
        g.MapGet("/nodes/{id}/history", (string id, string? from, string? to, int? lastMinutes, int? page, int? pageSize, MeshNetwork m) =>
        {
            var node = m.ResolveNode(id);
            if (node is null) return Results.NotFound(NotFoundBody);
            if (node.IsGateway) return Results.BadRequest(new { error = "The gateway has no telemetry history." });

            DateTime? f = null, t = null;
            if (lastMinutes is not null)
            {
                if (lastMinutes <= 0 || lastMinutes > 60 * 24 * 30) return Results.BadRequest(new { error = "lastMinutes must be between 1 and 43200." });
                f = DateTime.UtcNow.AddMinutes(-lastMinutes.Value);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(from)) { if (!TryDate(from, out var fd)) return Results.BadRequest(new { error = "'from' is not a valid date-time." }); f = fd; }
                if (!string.IsNullOrWhiteSpace(to)) { if (!TryDate(to, out var td)) return Results.BadRequest(new { error = "'to' is not a valid date-time." }); t = td; }
                if (f is not null && t is not null && f > t) return Results.BadRequest(new { error = "'from' must not be later than 'to'." });
            }
            return Results.Ok(m.History(node, f, t, page ?? 1, pageSize ?? 25));
        });

        g.MapGet("/history/stats", (TelemetryHistoryIndex idx) => Results.Ok(idx.Stats()));

        // ── Benchmarks ──────────────────────────────────────────────────────────
        g.MapGet("/benchmark/tree", (int? points, int? queries) => Results.Ok(MeshBenchmarks.Tree(points ?? 50_000, queries ?? 200)));
        g.MapGet("/benchmark/graph", (int? nodes) => Results.Ok(MeshBenchmarks.Graph(nodes ?? 2_000)));
    }

    private static bool TryDate(string s, out DateTime dt)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt);

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}
