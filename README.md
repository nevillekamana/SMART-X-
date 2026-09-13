# Smart-X — Sensor Ingestion & Validation Gateway

Smart-X is a lightweight ASP.NET Core Minimal API application (migrated to .NET 10)
with an integrated static HTML/JS dashboard. It ingests, validates, and stores
simulated IoT telemetry from a distributed sensor mesh (temperature, power, actuator
state) and demonstrates safe file attachment uploads, meaningful batch consolidation,
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

## Key Technical Concepts Demonstrated

- **Generics** — `TelemetryPacket<T>` (constrained to `struct`) wraps `float`,
  `int`, and `bool` sensor values without boxing/unboxing. Implements `ITelemetryPacket`
  interface for strongly-typed generic storage.
- **Operator Overloading** — `SensorData` overloads `+`, `-`, `>`, `<`, `>=`, `<=`
  to allow direct aggregation (`Meter3 = Meter1 + Meter2`) and delta/threshold
  comparisons.
- **Advanced Arrays** — Telemetry readings are first buffered into `_pendingBatchBuffer`
  (committed every 10 readings) then transferred into an optimised `List<double>`.
  Raw batches stored in `double[][]` jagged array.
- **HashSet-Based Deduplication** — `ConsolidateAllBatches()` uses `HashSet<double>`
  for O(1) duplicate detection instead of O(n) `List.Contains()` checks.
- **Recursion** — `DeploymentValidationService` recursively validates, searches,
  counts, and renders a multi-tier deployment tree
  (`Facility → Zone → Sub-Zone → Room → Node`).
- **Sensor Validation** — Telemetry push validates that sender SensorId is registered,
  category matches (Environmental/PowerConsumption/Actuator), and rejects with
  appropriate HTTP status (404 for unregistered, 400 for category mismatch).

## Security & Validation Highlights

- **Telemetry Category Validation** — `POST /api/telemetry/push/{type}` validates
  that the SensorId is registered and matches the expected category (e.g., "power"
  telemetry rejected if sensor registered as "Environmental"). Returns 404 for
  unregistered sensors, 400 for category mismatch.
- **Sensor Registration Validation** — MAC address uniqueness checked; all required
  fields validated before accepting a sensor.
- **Strongly-Typed Packet Storage** — `TelemetryPacket<T>` implements `ITelemetryPacket`
  interface; generic storage avoids boxing/unboxing and strengthens type safety.
- **Meaningful Jagged-Array Batching** — Telemetry readings buffered into
  `_pendingBatchBuffer` and committed as one row to `double[][]` when buffer reaches
  10 readings. Real-time views never delayed.
- **HashSet-Based Consolidation** — `ConsolidateAllBatches()` uses `HashSet<double>`
  for O(1) duplicate detection; GET `/api/telemetry/consolidate` endpoint demonstrates
  deduplication.
- **Unit-Checked Comparisons** — `/api/telemetry/compare` throws
  `InvalidOperationException` if sensors have different units (e.g., °C vs W).
- **Global Exception Handler** — All unhandled exceptions caught by middleware;
  API always returns clean JSON responses with no stack traces in Development or
  Production mode.
- **Malformed JSON Handling** — `ReadFromJsonAsync` wrapped in try/catch for
  `System.Text.Json.JsonException`; malformed requests return 400 with safe message.
- **Numeric Sanity Checks** — Float values reject NaN/Infinity; integer power
  readings reject negative values.
- **File Upload Hardening** — Extension allow-list, Content-Type parameter stripping,
  magic-byte signature validation (PNG/JPEG/PDF), filename sanitisation, size limits.

---

## API Endpoints (summary)

| Method | Route | Purpose |
|--------|-------|---------|
| POST | `/api/sensors/register` | Register a new sensor (validates category, MAC uniqueness) |
| GET  | `/api/sensors` | List all registered sensors |
| GET  | `/api/sensors/{id}` | Get a sensor by internal ID |
| GET  | `/api/sensors/mac/{mac}` | Get a sensor by MAC address |
| POST | `/api/sensors/{id}/attachments` | Upload a config/photo/log file (multipart) |
| GET  | `/api/sensors/{id}/attachments` | List a sensor's attachments |
| POST | `/api/telemetry/push/{type}` | Push telemetry (`environmental`, `power`, `actuator`); validates SensorId & category match |
| GET  | `/api/telemetry/latest` | Latest 50 readings |
| GET  | `/api/telemetry/history` | Full consolidated reading history + batch info |
| GET  | `/api/telemetry/consolidate` | Consolidated deduplicated history (HashSet-based) |
| POST | `/api/telemetry/aggregate` | Aggregate two sensors via overloaded `+` (unit must match) |
| POST | `/api/telemetry/delta` | Delta two sensors via overloaded `-` (unit must match) |
| POST | `/api/telemetry/compare` | Compare two sensors via overloaded `>`/`<` (unit must match) |
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

## Verification Test Results

Tests executed against a running local instance (http://localhost:59185, .NET 10, Development environment).

### Critical Validation Tests

| Test Case | Endpoint | Request | Expected | Result | Notes |
|---|---|---|---|---|---|
| Push telemetry for registered sensor | POST /api/telemetry/push/environmental | Valid sensor ID, float temp value | 200 OK, IsValidated=true | ✅ PASS | Sensor must exist and category must match |
| Push telemetry for unregistered sensor | POST /api/telemetry/push/environmental | Fake sensor ID | 404 Not Found | ✅ PASS | ValidateTelemetryType throws KeyNotFoundException |
| Push telemetry with category mismatch | POST /api/telemetry/push/power | Sensor registered as "Environmental" | 400 Bad Request | ✅ PASS | ValidateTelemetryType throws InvalidOperationException |
| Malformed JSON (Development mode) | POST /api/telemetry/push/environmental | Invalid JSON body | 400 Bad Request, no stack trace | ✅ PASS | JsonException caught; error message only |
| Malformed JSON (Production mode) | POST /api/telemetry/push/environmental | Invalid JSON body | 400 Bad Request, no stack trace | ✅ PASS | Global exception handler returns clean JSON |

### Consolidation & Deduplication Tests

| Test Case | Endpoint | Request | Expected | Result | Notes |
|---|---|---|---|---|---|
| Consolidate endpoint returns deduplicated values | GET /api/telemetry/consolidate | None | 200 OK, unique value count, values array | ✅ PASS | HashSet-based O(1) dedup working |
| Multiple readings coalesced into batches | GET /api/telemetry/history | After 10+ simulated readings | batchCount ≥ 1, totalReadings ≥ 10 | ✅ PASS | Buffer commits every 10 readings |

### Operator Overloading Tests

| Test Case | Endpoint | Request | Expected | Result | Notes |
|---|---|---|---|---|---|
| Aggregate two sensors (matching units) | POST /api/telemetry/aggregate | Two sensor IDs, both °C | 200 OK, sum value | ✅ PASS | Uses overloaded + operator |
| Aggregate missing sensor | POST /api/telemetry/aggregate | Non-existent sensor ID | 404 Not Found | ✅ PASS | KeyNotFoundException caught |
| Aggregate with missing sensor ID param | POST /api/telemetry/aggregate | Empty/null SensorId1 or 2 | 400 Bad Request | ✅ PASS | ArgumentException from ValidateSensorIdsProvided |
| Delta two sensors | POST /api/telemetry/delta | Two sensor IDs | 200 OK, difference | ✅ PASS | Uses overloaded - operator |
| Compare two sensors (same unit) | POST /api/telemetry/compare | Two sensor IDs, both °C or both W | 200 OK, >/</== result | ✅ PASS | Uses overloaded comparison operators |
| Compare sensors (mismatched units) | POST /api/telemetry/compare | One °C, one W | 400 Bad Request | ✅ PASS | Unit check throws InvalidOperationException |

### File Upload Tests

| Test Case | Endpoint | Request | Expected | Result | Notes |
|---|---|---|---|---|---|
| Valid PDF upload | POST /api/sensors/{id}/attachments | valid.pdf | 200 OK, file saved to wwwroot/uploads/{id}/ | ✅ PASS | Magic-byte check passes |
| Valid PNG upload | POST /api/sensors/{id}/attachments | valid.png (real PNG header) | 200 OK | ✅ PASS | PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A |
| Valid JPEG upload | POST /api/sensors/{id}/attachments | valid.jpg (real JPEG header) | 200 OK | ✅ PASS | JPEG magic bytes: FF D8 FF |
| Invalid PNG (bad magic) | POST /api/sensors/{id}/attachments | invalid_magic.png | 400 Bad Request | ✅ PASS | Signature validation working |
| .log file with octet-stream | POST /api/sensors/{id}/attachments | .log, Content-Type: application/octet-stream | 200 OK | ✅ PASS | octet-stream now accepted (fixed) |
| Content-Type parameter stripping | POST /api/sensors/{id}/attachments | .json with "application/json; charset=utf-8" | 200 OK | ✅ PASS | Parameters stripped before comparison |

### Deployment & Recursion Tests

| Test Case | Endpoint | Request | Expected | Result | Notes |
|---|---|---|---|---|---|
| Render deployment tree | GET /api/deployment/tree | None | 200 OK, tree structure | ✅ PASS | No regression; recursion still working |
| Validate deployment tree | POST /api/deployment/validate | None | 200 OK, isValid=false with warnings | ✅ PASS | Over-capacity nodes detected |
| Find node in tree | GET /api/deployment/find/{name} | Valid node name | 200 OK, node details | ✅ PASS | Recursive search working |

### Summary

- **17/17 critical test scenarios PASSED**
- No stack traces leaked in Development or Production mode
- All validation exceptions mapped to correct HTTP status codes
- File upload magic-byte validation still working (no false positives)
- Operator overloading functions correctly with unit checking
- Deployment tree recursion unaffected by telemetry changes


---

## Known Limitations (Part 1 scope)

- Data is stored in-memory only — restarting the app clears all state.
- The project is intended for demonstration and teaching; it is not a production
  hardened storage service. Use appropriate secure storage for real deployments.

---

## Author

Neville Kabamba ST10404539 — PROG7312 PART 1

