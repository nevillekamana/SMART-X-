using SmartX.Models.Streaming;
using SmartX.Services.Streaming;

namespace SmartX.Endpoints;

/// <summary>All Part 2 routes live under /api/stream.</summary>
public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/stream").WithTags("Part 2 — Real-Time Command Stream");

        // ── Devices (Dictionary lookups + search / filter) ──────────────────────
        g.MapGet("/devices", (string? q, string? category, string? state, IngestionPipeline p)
            => Results.Ok(p.QueryDevices(q, category, state)));

        g.MapGet("/devices/{id}", (string id, IngestionPipeline p) =>
        {
            var d = p.GetDevice(id);
            return d is null ? Results.NotFound(new { error = "Device not found." }) : Results.Ok(d);
        });

        // Timeline from the SortedDictionary — already in timestamp order.
        g.MapGet("/devices/{id}/history", (string id, int? limit, IngestionPipeline p, DeviceRegistry r) =>
        {
            var d = p.GetDevice(id);
            if (d is null) return Results.NotFound(new { error = "Device not found." });
            var n = Math.Clamp(limit ?? 120, 1, 600);
            var points = r.GetHistory(d.Mac, n);
            return Results.Ok(new { device = d, count = points.Count, totalStored = r.HistoryCount(d.Mac), points });
        });

        // ── Ingestion (what a device calls) ─────────────────────────────────────
        g.MapPost("/ingest", (IngestRequest req, IngestionPipeline p) =>
        {
            var r = p.Ingest(req);
            return r.Accepted
                ? Results.Accepted(null, new { status = "queued", lane = r.Lane, sequence = r.Sequence, standardDepth = r.StandardDepth, priorityDepth = r.PriorityDepth })
                : Results.Json(new { error = r.Error }, statusCode: r.StatusCode);
        });

        g.MapPost("/ingest/batch", (List<IngestRequest> batch, IngestionPipeline p) =>
        {
            if (batch is null || batch.Count == 0) return Results.BadRequest(new { error = "Send a non-empty JSON array of packets." });
            if (batch.Count > 500) return Results.BadRequest(new { error = "Maximum 500 packets per batch." });

            int accepted = 0, priority = 0, rejected = 0;
            var errors = new List<string>();
            foreach (var req in batch)
            {
                var r = p.Ingest(req);
                if (r.Accepted) { accepted++; if (r.Lane == "Priority") priority++; }
                else { rejected++; if (errors.Count < 5 && r.Error is not null) errors.Add(r.Error); }
            }
            return Results.Accepted(null, new { accepted, priority, standard = accepted - priority, rejected, errors });
        });

        // ── Pipeline monitor ────────────────────────────────────────────────────
        g.MapGet("/queues", (IngestionPipeline p) => Results.Ok(p.GetQueueSnapshot()));
        g.MapPost("/processor/pause", (IngestionPipeline p) => { p.Paused = true; return Results.Ok(new { paused = true }); });
        g.MapPost("/processor/resume", (IngestionPipeline p) => { p.Paused = false; return Results.Ok(new { paused = false }); });

        // ── Alerts (HashSet) ────────────────────────────────────────────────────
        g.MapGet("/alerts", (IngestionPipeline p) => Results.Ok(p.GetAlertSnapshot()));

        g.MapPost("/disconnect-reports", (DisconnectReportRequest req, IngestionPipeline p) =>
        {
            var mac = p.ResolveMac(req.DeviceId);
            if (mac is null) return Results.NotFound(new { error = "Unknown device." });
            var (isNew, name, dupes) = p.ReportDisconnect(mac, req.ReportedBy);
            return Results.Ok(new { device = name, isNew, duplicatesSuppressed = dupes });
        });

        // ── Commands (Stack) ────────────────────────────────────────────────────
        g.MapPost("/commands", (CommandRequest req, IngestionPipeline p, CommandService c) =>
        {
            var mac = p.ResolveMac(req.DeviceId);
            if (mac is null) return Results.NotFound(new { error = "Unknown device." });
            return Guard(() => Results.Ok(c.Execute(mac, req.Action, req.Operator)));
        });

        g.MapPost("/commands/bulk", (BulkCommandRequest req, CommandService c)
            => Guard(() => Results.Ok(c.ExecuteBulk(req.Kind, req.Action, req.Operator))));

        g.MapPost("/commands/undo", (CommandService c) => Guard(() => Results.Ok(c.Undo())));
        g.MapGet("/commands", (CommandService c) => Results.Ok(c.GetSnapshot()));

        // ── Search log + predictive engine ──────────────────────────────────────
        g.MapPost("/events/search", (SearchEventRequest req, IngestionPipeline p, PredictiveEngine e) =>
        {
            var q = req.Query?.Trim() ?? "";
            if (q.Length < 2) return Results.Ok(new { matched = 0, logged = false });

            var matches = p.QueryDevices(q, null, null);
            // Learn only from unambiguous searches (one device, or an exact name match).
            var target = matches.Count == 1 ? matches[0]
                       : matches.FirstOrDefault(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (target is not null) e.RecordSearch(target.Name);
            return Results.Ok(new { matched = matches.Count, logged = target is not null, device = target?.Name });
        });

        g.MapGet("/insights", (IngestionPipeline p, PredictiveEngine e) => Results.Ok(e.GetInsights(p.GetActiveAlerts())));
    }

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}
