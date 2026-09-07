# Smart-X IoT Mesh Ecosystem — Part 1: Data Ingestion & Validation Gateway

A .NET 8 ASP.NET Core Minimal API backend with an integrated HTML/JS dashboard for
ingesting, validating, and visualising simulated IoT telemetry from a distributed
sensor mesh (temperature, power consumption, actuator states, etc).

This README covers **Part 1** of the Smart-X PoE (PROG7312 / AAPD7112): the Sensor
Data Ingestion and Telemetry pillar. The other two pillars (Real-Time Command Stream,
Network Topology & Mesh Routing) are visible on the startup screen but intentionally
disabled — they are implemented in later parts of the PoE.

---

## Tech Stack

| Layer      | Technology |
|------------|------------|
| Backend    | ASP.NET Core 8 Minimal APIs |
| API Docs   | Swashbuckle / Swagger UI |
| Frontend   | Vanilla HTML5, CSS3, JavaScript (served as static files by the same app) |
| Storage    | In-memory (Dictionary/List-backed services — no external DB required) |

The frontend is served directly from `wwwroot/` by the same ASP.NET Core process
that hosts the API, so there is only **one process to run**.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) installed
- Any modern browser (Chrome, Edge, Firefox)
- No database, Docker, or Node.js required for Part 1

Verify your SDK version:

```bash
dotnet --version
# should print 10.x.x
```

---

## Getting Started

### 1. Clone the repository

```bash
git clone <your-repo-url>
cd SmartX/SmartX
```

### 2. Restore dependencies

```bash
dotnet restore
```

### 3. Build the project

```bash
dotnet build
```

### 4. Run the application

```bash
dotnet run
```

By default the app will start on the URLs configured in
`Properties/launchSettings.json`:

- `https://localhost:59184`
- `http://localhost:59185`

(Ports may differ on your machine — check the console output when you run `dotnet run`.)

### 5. Open the dashboard

Navigate to the HTTPS URL printed in the console. You'll land on the Smart-X
startup screen. Click the **Sensor Data Ingestion & Telemetry** pillar to enter
the gateway dashboard.

### 6. Explore the API directly (optional)

Swagger UI is available at:

```
https://localhost:<port>/swagger
```

This lets you call every endpoint below without the dashboard.

---

## Project Structure

```
SmartX/
├── Program.cs                        # Minimal API endpoint definitions & app startup
├── Models/
│   ├── TelemetryPacket.cs            # Generic TelemetryPacket<T> (Generics)
│   ├── SensorData.cs                 # Operator-overloaded sensor readings
│   ├── SensorRegistration.cs         # Sensor registration + attachment records
│   └── DeploymentNode.cs             # Recursive deployment tree node + validation result
├── Services/
│   ├── TelemetryService.cs           # Jagged arrays → List<T>, packet storage, aggregation
│   ├── DeploymentValidationService.cs# Recursive tree validation / search / render
│   └── FileUploadService.cs          # Multipart file upload handling for attachments
└── wwwroot/
    ├── index.html                    # Startup screen + dashboard shell
    ├── css/dashboard.css
    └── js/dashboard.js                # All dashboard interactivity & API calls
```

---

## Key Technical Concepts Demonstrated

- **Generics** — `TelemetryPacket<T>` (constrained to `struct`) wraps `float`,
  `int`, and `bool` sensor values without boxing/unboxing.
- **Operator Overloading** — `SensorData` overloads `+`, `-`, `>`, `<`, `>=`, `<=`
  to allow direct aggregation (`Meter3 = Meter1 + Meter2`) and delta/threshold
  comparisons.
- **Advanced Arrays** — Telemetry readings are first collected into a jagged
  `double[][]` batch store before being transferred into an optimised `List<double>`.
- **Recursion** — `DeploymentValidationService` recursively validates, searches,
  counts, and renders a multi-tier deployment tree
  (`Facility → Zone → Sub-Zone → Room → Node`).
- **Dynamic Engagement Feature** — A live "pulse" animation plus a proactive
  "Needs attention" panel surface anomalies (stale sensors, capacity breaches)
  without the user having to search for them.

---

## API Endpoints (summary)

| Method | Route | Purpose |
|--------|-------|---------|
| POST | `/api/sensors/register` | Register a new sensor |
| GET  | `/api/sensors` | List all registered sensors |
| GET  | `/api/sensors/{id}` | Get a sensor by internal ID |
| GET  | `/api/sensors/mac/{mac}` | Get a sensor by MAC address |
| POST | `/api/sensors/{id}/attachments` | Upload a config/photo/log file (multipart) |
| GET  | `/api/sensors/{id}/attachments` | List a sensor's attachments |
| POST | `/api/telemetry/push/{type}` | Push telemetry (`environmental`, `power`, `actuator`) |
| GET  | `/api/telemetry/latest` | Latest 50 readings |
| GET  | `/api/telemetry/history` | Full consolidated reading history + batch info |
| POST | `/api/telemetry/aggregate` | Aggregate two sensors via overloaded `+` |
| POST | `/api/telemetry/delta` | Delta two sensors via overloaded `-` |
| POST | `/api/telemetry/compare` | Compare two sensors via overloaded `>`/`<` |
| POST | `/api/telemetry/simulate` | Generate 5 randomised telemetry readings |
| GET  | `/api/deployment/tree` | Render the full deployment tree |
| POST | `/api/deployment/validate` | Recursively validate the deployment tree |
| GET  | `/api/deployment/find/{name}` | Recursively find a node by name/ID |

Full request/response schemas are available in Swagger UI once the app is running.

---

## Testing the Application

1. Run `dotnet run` and open the dashboard.
2. Click **Simulate activity** on the Overview tab to generate sample telemetry.
3. Go to **Sensors** and register a device (e.g. MAC `A4:78:9C:12:8F:21`).
4. Attach a file to that sensor from the same tab.
5. Go to **Telemetry** and push a manual reading, or simulate more.
6. Go to **Deployment** and click **Run validation** to see the recursive tree
   validator flag the intentionally over-capacity node (`Node A2`).
7. Go to **Operator** and pick two sensors of the same category to try the
   aggregate/delta/compare operators.

---

## Known Limitations (Part 1 scope)

- Data is stored in-memory only — restarting the app clears all state.
- Sensor registration currently does not reject duplicate MAC addresses server-side;
  this is a known gap being addressed as part of ongoing refinement.
- File uploads are not currently size/type-restricted server-side (client-side
  `accept` filter only).

---

## Author

Neville Kabamba ST10404539 — PROG7312 PART 1
