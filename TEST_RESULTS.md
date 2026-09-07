# Test Results — Final Verification Run

Date: 2026-09-07T23:21:03Z

Server: http://localhost:59185

Sensor used: 17ef1035-453f-4d85-af92-551c83572e1d

Summary:
- valid.pdf -> 200 OK, file written to wwwroot/uploads/{sensor}/valid_*.pdf
- valid.png -> 400 Bad Request (signature mismatch)
- valid.jpg -> 400 Bad Request (signature mismatch)
- invalid_magic.png -> 400 Bad Request (signature mismatch)

Deployment endpoints:
- /api/deployment/tree -> 200 OK (rendered tree)
- /api/deployment/validate -> 200 OK (isValid=false with expected errors/warnings)
- /api/deployment/find/Node%20A1 -> 200 OK (found node)

Telemetry:
- /api/telemetry/simulate -> 200 OK (generated sample readings)
- /api/telemetry/latest -> 200 OK (returned recent entries)

Notes:
- No HTTP 500s or stack traces leaked during verification.
- Uploads directory contains only the accepted PDF file.
