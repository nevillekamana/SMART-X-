using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using SmartX.Endpoints;
using SmartX.Models;
using SmartX.Services;
using SmartX.Services.Streaming;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<DeploymentValidationService>();
builder.Services.AddScoped<FileUploadService>();

// ── Part 2: real-time command stream ──
builder.Services.AddSingleton<SmartX.Services.Mesh.TelemetryHistoryIndex>();   // AVL history archive (final PoE)
builder.Services.AddSingleton<DeviceRegistry>();       // Dictionary + SortedDictionary
builder.Services.AddSingleton<PredictiveEngine>();     // transition model, forecasts, risk heap
builder.Services.AddSingleton<IngestionPipeline>();    // Queue, PriorityQueue, HashSet
builder.Services.AddSingleton<CommandService>();       // Stack (undo)
builder.Services.AddHostedService<DeviceSeeder>();     // demo fleet + history
builder.Services.AddHostedService<StreamProcessor>();  // drains the queues

// Add Swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Smart-X Ingestion API", Version = "v1" });
});

// CORS for dashboard access
builder.Services.AddCors(o => o.AddPolicy("AllowAll", p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ── Global Exception Handler ────────────────────────────
// Catches unhandled exceptions and returns clean JSON response
// without exposing stack traces in any environment
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred while processing the request." });
    });
});

app.UseCors("AllowAll");
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Smart-X API v1"));

// Serve static files (dashboard)
app.UseDefaultFiles();
app.UseStaticFiles();

// ══════════════════════════════════════════════════════════
//  API ENDPOINTS
// ══════════════════════════════════════════════════════════

var telemetry = app.MapGroup("/api/telemetry");
var sensors = app.MapGroup("/api/sensors");
var deployment = app.MapGroup("/api/deployment");

// ── Sensor Registration ──────────────────────────────────

sensors.MapPost("/register", (SensorRegistration reg, TelemetryService svc) =>
{
    try
    {
        var registered = svc.RegisterSensor(reg);
        return Results.Ok(new { message = "Sensor registered", sensor = registered });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

sensors.MapGet("/", (TelemetryService svc) =>
{
    return Results.Ok(svc.GetAllSensors());
});

sensors.MapGet("/{id}", (string id, TelemetryService svc) =>
{
    var sensor = svc.GetSensor(id);
    return sensor is not null ? Results.Ok(sensor) : Results.NotFound(new { error = "Sensor not found" });
});

sensors.MapGet("/mac/{mac}", (string mac, TelemetryService svc) =>
{
    var sensor = svc.GetSensorByMac(mac);
    return sensor is not null ? Results.Ok(sensor) : Results.NotFound(new { error = "Sensor not found" });
});

// ── Sensor File Attachment Upload ─────────────────────────

// File upload endpoint: antiforgery disabled for multipart uploads
sensors.MapPost("/{id}/attachments", async (string id, IFormFile file, TelemetryService svc, FileUploadService fsvc) =>
{
    var sensor = svc.GetSensor(id);
    if (sensor is null) return Results.NotFound(new { error = "Sensor not found" });

    if (file is null) return Results.BadRequest(new { error = "No file provided in the request" });

    try
    {
        using var stream = file.OpenReadStream();
        var attachment = await fsvc.UploadFileAsync(id, stream, file.FileName, file.ContentType, file.Length);
        sensor.Attachments.Add(attachment);
        return Results.Ok(new { message = "File attached", attachment });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

sensors.MapGet("/{id}/attachments", (string id, TelemetryService svc) =>
{
    var sensor = svc.GetSensor(id);
    if (sensor is null) return Results.NotFound(new { error = "Sensor not found" });
    return Results.Ok(sensor.Attachments);
});

// ── Telemetry Data Push ─────────────────────────────────

// Generic endpoint for pushing typed telemetry data (demonstrates Generics)
telemetry.MapPost("/push/{type}", async (string type, HttpContext ctx, TelemetryService svc) =>
{
    try
    {
        switch (type.ToLower())
        {
            case "temperature":
            case "humidity":
            case "environmental":
                try
                {
                    var envData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<float>>();
                    if (envData is null)
                        return Results.BadRequest(new { error = "Invalid environmental telemetry payload" });

                    // Validate sensor exists and category matches
                    var sensor = svc.ValidateTelemetryType(envData.SensorId, type);

                    // Basic sanity checks for float values: reject NaN/Infinity
                    if (float.IsNaN(envData.Value) || float.IsInfinity(envData.Value))
                        return Results.BadRequest(new { error = "Environmental values cannot be NaN or Infinity." });

                    envData.SensorCategory = "Environmental";
                    envData.IsValidated = true;
                    svc.StorePacket(envData);
                    svc.AddSensorData(new SensorData(envData.SensorId, "Environmental", envData.Location, envData.Value, "°C", envData.SignalStrength));
                    return Results.Ok(new { message = "Environmental telemetry stored", packet = envData.ToString() });
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Malformed JSON in request body." });
                }

            case "power":
            case "powerconsumption":
                try
                {
                    var pwrData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<int>>();
                    if (pwrData is null)
                        return Results.BadRequest(new { error = "Invalid power telemetry payload" });

                    // Validate sensor exists and category matches
                    var sensor = svc.ValidateTelemetryType(pwrData.SensorId, type);

                    // Basic sanity checks for int values: reject negative power readings
                    if (pwrData.Value < 0)
                        return Results.BadRequest(new { error = "Power consumption readings cannot be negative." });

                    pwrData.SensorCategory = "PowerConsumption";
                    pwrData.IsValidated = true;
                    svc.StorePacket(pwrData);
                    svc.AddSensorData(new SensorData(pwrData.SensorId, "PowerConsumption", pwrData.Location, pwrData.Value, "W", pwrData.SignalStrength));
                    return Results.Ok(new { message = "Power telemetry stored", packet = pwrData.ToString() });
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Malformed JSON in request body." });
                }

            case "actuator":
            case "switch":
                try
                {
                    var actData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<bool>>();
                    if (actData is null)
                        return Results.BadRequest(new { error = "Invalid actuator telemetry payload" });

                    // Validate sensor exists and category matches
                    var sensor = svc.ValidateTelemetryType(actData.SensorId, type);

                    actData.SensorCategory = "Actuator";
                    actData.IsValidated = true;
                    svc.StorePacket(actData);
                    svc.AddSensorData(new SensorData(actData.SensorId, "Actuator", actData.Location, actData.Value ? 1 : 0, "state", actData.SignalStrength));
                    return Results.Ok(new { message = "Actuator telemetry stored", packet = actData.ToString() });
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Malformed JSON in request body." });
                }

            default:
                return Results.BadRequest(new { error = $"Unknown sensor type: {type}. Supported: environmental, power, actuator" });
        }
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Telemetry Data Retrieval ─────────────────────────────

telemetry.MapGet("/latest", (TelemetryService svc) =>
{
    return Results.Ok(svc.GetAllSensorData().OrderByDescending(d => d.Timestamp).Take(50));
});

// ── Needs Attention (proactive alert prioritisation) ─────
// Computed server-side against O(1) dictionary lookups (registered sensors +
// latest reading per sensor) rather than filtered client-side over a flat array.
telemetry.MapGet("/needs-attention", (TelemetryService svc) =>
{
    return Results.Ok(svc.GetNeedsAttention());
});

telemetry.MapGet("/history", (TelemetryService svc) =>
{
    var (batchCount, totalReadings, _) = svc.GetBatchInfo();
    return Results.Ok(new
    {
        batchCount,
        totalReadings,
        history = svc.GetHistory()
    });
});

// ── Operator Overloading Demo Endpoints ─────────────────

telemetry.MapPost("/aggregate", (AggregateRequest req, TelemetryService svc) =>
{
    try
    {
        var result = svc.AggregateSensors(req.SensorId1, req.SensorId2);
        return Results.Ok(new { message = "Aggregated using operator +", result = result.ToString() });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

telemetry.MapPost("/delta", (AggregateRequest req, TelemetryService svc) =>
{
    try
    {
        var result = svc.DeltaSensors(req.SensorId1, req.SensorId2);
        return Results.Ok(new { message = "Delta using operator -", result = result.ToString() });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

telemetry.MapPost("/compare", (AggregateRequest req, TelemetryService svc) =>
{
    try
    {
        var result = svc.CompareSensors(req.SensorId1, req.SensorId2);
        return Results.Ok(new { message = "Compared using operators > <", result });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Telemetry Consolidation with Deduplication ─────────

telemetry.MapGet("/consolidate", (TelemetryService svc) =>
{
    var consolidated = svc.ConsolidateAllBatches();
    return Results.Ok(new
    {
        message = "Consolidated telemetry with deduplication",
        uniqueValueCount = consolidated.Count,
        values = consolidated
    });
});

// ── Simulation ─────────────────────────────────────────

telemetry.MapPost("/simulate", (TelemetryService svc) =>
{
    svc.SimulateTelemetry();
    return Results.Ok(new { message = "Simulated telemetry generated", dataCount = svc.GetAllSensorData().Count });
});

// ── Deployment Tree Validation (Recursion) ─────────────

deployment.MapGet("/tree", (DeploymentValidationService dsvc) =>
{
    return Results.Ok(new
    {
        tree = dsvc.RenderTree(),
        nodeCount = dsvc.CountNodes()
    });
});

deployment.MapPost("/validate", (DeploymentValidationService dsvc) =>
{
    var result = dsvc.ValidateTree();
    return Results.Ok(result);
});

deployment.MapGet("/find/{name}", (string name, DeploymentValidationService dsvc) =>
{
    var node = dsvc.FindNode(name);
    return node is not null ? Results.Ok(node) : Results.NotFound(new { error = $"Node '{name}' not found" });
});

// ── Part 2: /api/stream/* ──
app.MapStreamEndpoints();

// ── Dashboard Fallback ─────────────────────────────────

app.MapFallbackToFile("index.html");

app.Run();

// ── Request DTOs ────────────────────────────────────────

public record AggregateRequest(string SensorId1, string SensorId2);
