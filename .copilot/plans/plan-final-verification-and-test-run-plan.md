# Final verification and test-run plan
## Understanding
Run full verification against the migrated .NET 10 SmartX app: start the server, execute the file-upload test matrix and deployment/recursion endpoint tests, perform basic frontend end-to-end checks, record results, and update README test table.

## Assumptions
- The project builds under .NET 10 and dotnet CLI is available in the environment.
- The repository root is the current working directory and contains SmartX.csproj.
- The test payloads are available under test_files/.

## Approach
1. Start the SmartX server and confirm listening endpoints.
2. Execute the prepared upload test matrix (valid and invalid files) against the POST /api/sensors/{id}/attachments endpoint and record HTTP status, response body, and disk writes.
3. Run deployment/recursion endpoint checks (/api/deployment/tree, /validate, /find/{name}) including boundary cases.
4. Perform critical frontend flows manually via automated fetches where possible (register sensor, simulate telemetry, upload via API), confirm toasts appear and no stack traces leak.
5. Add a concise test-result table to README.md and commit changes.

## Key Files
- Program.cs - endpoints, including upload endpoint with .DisableAntiforgery()
- Services/FileUploadService.cs - upload validation and disk save logic
- test_files/* - payloads for upload tests
- README.md - update with test results

## Risks & Open Questions
- Server might not run due to file locks or blocked DLLs; will stop existing processes and rebuild if needed.
- Some tests require a valid sensorId; if no sensors exist, create one via the registration endpoint before uploads.

## Steps
1. Start SmartX server (dotnet run --project SmartX.csproj) and confirm it's listening. 
2. Ensure at least one registered sensor exists (POST /api/sensors/register) and capture the sensorId. 
3. Run upload test matrix: POST valid.png, valid.jpg, valid.pdf, invalid_magic.png, mismatched MIME and path-traversal attempt; record outcomes and check uploads directory. 
4. Run deployment/recursion endpoint tests: GET /api/deployment/tree, POST /api/deployment/validate, GET /api/deployment/find/{name} (existing + non-existing). 
5. Run basic telemetry/frontend flows: register sensor (if not present), POST /api/telemetry/simulate, fetch /api/telemetry/latest, and perform an attachment upload via the dashboard.js API (fetch) simulation if possible. 
6. Update README.md with a short test-results table and commit. 
7. Stop the server background process and summarize results in a short report file (TEST_RESULTS.md).
