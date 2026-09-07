using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using SmartX.Models;
using SmartX.Services;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<DeploymentValidationService>();
builder.Services.AddScoped<FileUploadService>();

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

sensors.MapPost("/{id}/attachments", async (string id, IFormFile file, TelemetryService svc, FileUploadService fsvc) =>
{
    var sensor = svc.GetSensor(id);
    if (sensor is null) return Results.NotFound(new { error = "Sensor not found" });

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
});

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
    switch (type.ToLower())
    {
        case "temperature":
        case "humidity":
        case "environmental":
            var envData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<float>>();
            if (envData is not null)
            {
                envData.SensorCategory = "Environmental";
                envData.IsValidated = true;
                svc.StorePacket(envData);
                // Also add to SensorData store for operator overloading demos
                svc.AddSensorData(new SensorData(envData.SensorId, "Environmental", envData.Location, envData.Value, "°C"));
                return Results.Ok(new { message = "Environmental telemetry stored", packet = envData.ToString() });
            }
            return Results.BadRequest(new { error = "Invalid environmental telemetry payload" });

        case "power":
        case "powerconsumption":
            var pwrData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<int>>();
            if (pwrData is not null)
            {
                pwrData.SensorCategory = "PowerConsumption";
                pwrData.IsValidated = true;
                svc.StorePacket(pwrData);
                svc.AddSensorData(new SensorData(pwrData.SensorId, "PowerConsumption", pwrData.Location, pwrData.Value, "W"));
                return Results.Ok(new { message = "Power telemetry stored", packet = pwrData.ToString() });
            }
            return Results.BadRequest(new { error = "Invalid power telemetry payload" });

        case "actuator":
        case "switch":
            var actData = await ctx.Request.ReadFromJsonAsync<TelemetryPacket<bool>>();
            if (actData is not null)
            {
                actData.SensorCategory = "Actuator";
                actData.IsValidated = true;
                svc.StorePacket(actData);
                svc.AddSensorData(new SensorData(actData.SensorId, "Actuator", actData.Location, actData.Value ? 1 : 0, "state"));
                return Results.Ok(new { message = "Actuator telemetry stored", packet = actData.ToString() });
            }
            return Results.BadRequest(new { error = "Invalid actuator telemetry payload" });

        default:
            return Results.BadRequest(new { error = $"Unknown sensor type: {type}. Supported: environmental, power, actuator" });
    }
});

// ── Telemetry Data Retrieval ─────────────────────────────

telemetry.MapGet("/latest", (TelemetryService svc) =>
{
    return Results.Ok(svc.GetAllSensorData().OrderByDescending(d => d.Timestamp).Take(50));
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
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
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

// ── Dashboard Fallback ─────────────────────────────────

app.MapFallbackToFile("index.html");

app.Run();

// ── Request DTOs ────────────────────────────────────────

public record AggregateRequest(string SensorId1, string SensorId2);
