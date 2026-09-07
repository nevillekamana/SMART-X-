# HustleHub+ (Smart-X) — Sensor Ingestion & Validation Gateway

HustleHub+ (Smart-X) is a lightweight ASP.NET Core Minimal API application (migrated
to .NET 10) with an integrated static HTML/JS dashboard. It ingests, validates and
stores simulated IoT telemetry from a distributed sensor mesh (temperature, power,
actuator state) and demonstrates safe file attachment uploads, batch consolidation,
and recursive deployment validation.

This repository contains the Part 1 ingestion gateway used for teaching and
demonstration purposes. Additional pillars (real-time command stream, mesh
visualization) are intentionally disabled in the UI.

---

## Tech Stack

| Layer      | Technology |
|------------|------------|
| Backend    | ASP.NET Core 10 Minimal APIs |
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
# should print 10.x.x (e.g. 10.0.400)
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

## Key Technical Concepts Demonstrated (implemented)

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

## Security & Validation Highlights

- Server-side category validation (reject telemetry sent to wrong sensor category)
- Sensor registration validation and duplicate MAC checks
- Generic telemetry packet storage using strongly-typed interfaces
- Jagged-array batching with HashSet-based consolidation for performance
- File upload hardening: extension allow-list, content-type cross-checks, magic-byte
  signature validation (PNG/JPEG/PDF), filename/path sanitisation
- Global exception handling — API responses never leak stack traces
- Numeric sanity checks (NaN, negative power values rejected)

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

## Testing the Application (executed)

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

## Verification test results (summary)

The repository was exercised with a small verification matrix against a running local instance. Results below reflect tests executed during final verification (server on http://localhost:59185).

| Test case | Request | Expected | Result |
|---|---:|---|---|
| Valid PDF | POST /api/sensors/{id}/attachments (valid.pdf) | 200 OK, file written | 200 OK — file saved to wwwroot/uploads/{id}/ (valid_*.pdf)
| Valid PNG (test payload) | POST with valid.png | 200 OK | 400 Bad Request — signature mismatch (test file lacked valid PNG signature)
| Valid JPG (test payload) | POST with valid.jpg | 200 OK | 400 Bad Request — signature mismatch (test file lacked valid JPEG signature)
| Invalid signature PNG | POST with invalid_magic.png | reject | 400 Bad Request — signature mismatch
| MIME mismatch (png with image/jpeg) | POST with overridden MIME | reject | 400 Bad Request — signature check enforced

Notes:
- Only the PDF test file was accepted and written to disk during the verification run; the image test files included for quick testing did not contain real image headers and were correctly rejected by the magic-byte checks.
- No unexpected HTTP 500 responses or stack traces were observed for these flows; failures returned controlled 400 responses with safe messages.
- Deployment endpoints behaved as expected: /api/deployment/tree returned a rendered tree; /api/deployment/validate returned isValid=false with expected errors and warnings; /api/deployment/find/{name} returned matching nodes.
- Telemetry simulation (/api/telemetry/simulate) produced sample readings and /api/telemetry/latest returned recent entries.

How to reproduce (examples):

```powershell
# register (if needed)
curl.exe -X POST "http://localhost:59185/api/sensors/register" -H "Content-Type: application/json" -d '{"id":"sensor-001","macAddress":"AA:BB:CC:DD:EE:01","location":"Test Lab","category":"Environmental"}'

# upload (replace <id> with returned sensor id)
curl.exe -v -X POST "http://localhost:59185/api/sensors/<id>/attachments" -F "file=@test_files/valid.pdf"
curl.exe -v -X POST "http://localhost:59185/api/sensors/<id>/attachments" -F "file=@test_files/valid.png"
```

Artifacts created during verification:

- .copilot/plans/plan-final-verification-and-test-run-plan.md — this verification plan
- TEST_RESULTS.md — a short machine-readable summary of the run (created in the repo root)
- scripts/run-upload-tests.ps1 — helper PowerShell script to execute the upload matrix against a sensor id


---

## Known Limitations (Part 1 scope)

- Data is stored in-memory only — restarting the app clears all state.
- The project is intended for demonstration and teaching; it is not a production
  hardened storage service. Use appropriate secure storage for real deployments.

---

## Author

Neville Kabamba ST10404539 — PROG7312 PART 1

