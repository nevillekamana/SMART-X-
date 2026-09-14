# PROG7312 – Part 1 
# Smart-X – Sensor Ingestion and Validation Gateway

Smart-X is a small ASP.NET Core Minimal API project that I developed for Part 1 of PROG7312. The project was moved to .NET 10 and includes a simple HTML, CSS and JavaScript dashboard.

The main purpose of the project is to work with simulated IoT sensor data. The application can register sensors, receive and validate telemetry, store readings in memory, handle file attachments and validate a deployment tree using recursion.

Some of the other planned features, such as the real-time command stream and mesh visualisation, are not active in this version because they are outside the Part 1 scope.

---

## Technologies Used

| Part | Technology |
|---|---|
| Backend | ASP.NET Core 10 Minimal APIs |
| API documentation | Swagger / Swashbuckle |
| Frontend | HTML5, CSS3 and JavaScript |
| Storage | In-memory `Dictionary` and `List` collections |

The dashboard is inside the `wwwroot` folder, so the same ASP.NET Core application serves both the API and the frontend. This means I only need to run one application.

---

## Requirements

Before running the project, make sure you have:

- .NET 10 SDK
- A modern web browser such as Chrome, Edge or Firefox
- No database, Docker or Node.js is needed for this Part 1 project

To check the installed .NET version, run:

```bash
dotnet --version
```

The version should be `10.x.x`.

---

## Running the Project

### 1. Clone the repository

```bash
git clone <your-repo-url>
cd SmartX/SmartX
```

### 2. Restore the packages

```bash
dotnet restore
```

### 3. Build the project

```bash
dotnet build
```

### 4. Run the project

```bash
dotnet run
```

The application should start using the URLs in `Properties/launchSettings.json`.

The current development ports are:

- `https://localhost:59184`
- `http://localhost:59185`

The ports can be different on another computer, so the console output should be checked after running the application.

### 5. Open the dashboard

Open the HTTPS address shown in the console. The Smart-X start page should appear.

From there, select **Sensor Data Ingestion & Telemetry** to open the main dashboard.

### 6. Swagger

Swagger can also be used to test the API without using the dashboard:

```text
https://localhost:<port>/swagger
```

---

## Project Structure

```text
SmartX/
├── Program.cs
├── Models/
│   ├── TelemetryPacket.cs
│   ├── SensorData.cs
│   ├── SensorRegistration.cs
│   └── DeploymentNode.cs
├── Services/
│   ├── TelemetryService.cs
│   ├── DeploymentValidationService.cs
│   └── FileUploadService.cs
└── wwwroot/
    ├── index.html
    ├── css/
    │   └── dashboard.css
    └── js/
        └── dashboard.js
```

`Program.cs` contains the API setup and endpoint definitions.

The files in `Models` contain the main data structures used by the application.

The files in `Services` contain most of the application logic, such as telemetry handling, deployment validation and file uploads.

The `wwwroot` folder contains the dashboard files.

---

## Main C# Concepts Used

The project was also used to demonstrate some of the C# concepts required for the assignment.

### Generics

`TelemetryPacket<T>` is a generic class used for different sensor values. It is used with values such as:

- `float` for environmental readings
- `int` for power readings
- `bool` for actuator readings

It also implements the `ITelemetryPacket` interface.

### Operator Overloading

The `SensorData` class has overloaded operators such as:

```text
+
-
>
<
>=
<=
```

These are used when comparing or combining readings from sensors.

For example, two compatible sensor readings can be added using the `+` operator.

### Arrays and Lists

Telemetry readings are first collected into a temporary batch. After 10 readings, the batch is stored in a jagged `double[][]` array.

The readings are also kept in a `List<double>` for easier access when displaying the history.

### HashSet

`HashSet<double>` is used when consolidating the telemetry batches. This removes duplicate values without having to repeatedly search through a `List`.

### Recursion

The deployment structure is represented as a tree. The `DeploymentValidationService` uses recursion to:

- validate the tree
- search for nodes
- count nodes
- display the deployment structure

The tree has levels such as:

```text
Facility
  └── Zone
      └── Sub-Zone
          └── Room
              └── Node
```

### Sensor Validation

Before telemetry is stored, the application checks that:

1. The sensor has been registered.
2. The sensor ID exists.
3. The telemetry type matches the sensor category.

For example, a power reading should not be accepted for a sensor registered as an environmental sensor.

---

## Validation and Security

I added a number of checks to prevent incorrect data from being accepted.

### Telemetry Validation

The telemetry endpoint checks the sensor ID and category before storing the reading.

An unknown sensor results in a `404 Not Found`, while a category mismatch results in a `400 Bad Request`.

### Sensor Registration

Sensor registration checks required fields and makes sure that a MAC address is not already being used.

### JSON Validation

Malformed JSON requests are caught and return a `400 Bad Request` instead of exposing an exception or stack trace.

### Number Checks

Environmental readings reject `NaN` and `Infinity`.

Power consumption readings cannot be negative.

### File Uploads

Uploaded files are checked before they are saved. The application checks:

- allowed file extensions
- content type
- file size
- file name
- file signatures (magic bytes) for supported file types

PNG, JPEG and PDF files are checked using their file signatures instead of only trusting the file extension.

### Exception Handling

A global exception handler is used so that unexpected errors return a simple JSON error instead of exposing the application's stack trace.

---

## API Endpoints

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/sensors/register` | Register a sensor |
| GET | `/api/sensors` | Get all registered sensors |
| GET | `/api/sensors/{id}` | Get a sensor by ID |
| GET | `/api/sensors/mac/{mac}` | Get a sensor by MAC address |
| POST | `/api/sensors/{id}/attachments` | Add a file attachment |
| GET | `/api/sensors/{id}/attachments` | Get sensor attachments |
| POST | `/api/telemetry/push/{type}` | Send telemetry data |
| GET | `/api/telemetry/latest` | Get the latest 50 readings |
| GET | `/api/telemetry/history` | Get telemetry history |
| GET | `/api/telemetry/consolidate` | Get deduplicated telemetry |
| POST | `/api/telemetry/aggregate` | Add two sensor readings |
| POST | `/api/telemetry/delta` | Find the difference between readings |
| POST | `/api/telemetry/compare` | Compare two sensor readings |
| POST | `/api/telemetry/simulate` | Generate simulated readings |
| GET | `/api/deployment/tree` | Display the deployment tree |
| POST | `/api/deployment/validate` | Validate the deployment tree |
| GET | `/api/deployment/find/{name}` | Find a deployment node |

The full request and response information can be viewed through Swagger after the application has been started.

---

## Testing the Application

I tested the main functions by running the application locally and using the dashboard and API endpoints.

The basic testing process was:

1. Run the application using `dotnet run`.
2. Open the dashboard.
3. Use **Simulate activity** to create telemetry readings.
4. Register a sensor from the Sensors section.
5. Upload a file attachment to the sensor.
6. Send telemetry manually or generate more simulated readings.
7. Run the deployment validation.
8. Test the aggregate, delta and compare functions using two sensors.

The deployment test also checks the intentionally over-capacity `Node A2`.

---

## Test Results

### Telemetry Validation

| Test | Expected result | Result |
|---|---|---|
| Push telemetry for registered sensor | `200 OK` | PASS |
| Push telemetry for unregistered sensor | `404 Not Found` | PASS |
| Push telemetry with wrong category | `400 Bad Request` | PASS |
| Send malformed JSON in Development | `400 Bad Request` | PASS |
| Send malformed JSON in Production | `400 Bad Request` | PASS |

### Consolidation

| Test | Expected result | Result |
|---|---|---|
| Consolidate telemetry | Unique values returned | PASS |
| Store 10+ readings in batches | At least one batch created | PASS |

### Operator Overloading

| Test | Expected result | Result |
|---|---|---|
| Aggregate two compatible sensors | Sum returned | PASS |
| Aggregate missing sensor | `404 Not Found` | PASS |
| Missing sensor ID | `400 Bad Request` | PASS |
| Calculate delta | Difference returned | PASS |
| Compare compatible sensors | Comparison returned | PASS |
| Compare sensors with different units | `400 Bad Request` | PASS |

### File Uploads

| Test | Expected result | Result |
|---|---|---|
| Upload valid PDF | Accepted | PASS |
| Upload valid PNG | Accepted | PASS |
| Upload valid JPEG | Accepted | PASS |
| Upload PNG with incorrect file signature | Rejected | PASS |
| Upload `.log` using `application/octet-stream` | Accepted | PASS |
| Upload JSON with a content-type parameter | Accepted | PASS |

### Deployment Tree

| Test | Expected result | Result |
|---|---|---|
| Render deployment tree | Tree returned | PASS |
| Validate deployment tree | Invalid/over-capacity nodes detected | PASS |
| Find a deployment node | Node returned | PASS |

### Overall Result

The tests recorded for Part 1 resulted in:

**17/17 critical test scenarios passed.**

The tests also confirmed that:

- stack traces were not returned in the tested Development and Production scenarios
- validation errors returned the expected HTTP status codes
- file signature checking worked
- operator overloading worked with unit checking
- recursive deployment validation still worked

---

## Limitations

There are a few limitations with the current version:

- The data is stored in memory, so it is lost when the application is restarted.
- There is no external database in this Part 1 version.
- The application is mainly intended for the assignment/demo and is not a production-ready storage system.
- The real-time command stream and mesh visualisation are not enabled in this version.
- The dashboard is simple and does not have advanced features such as filterin

## VIDEO LINK
---

## Author

**Neville Kabamba**  
**ST10404539**  
**PROG7312 – Part 1**
