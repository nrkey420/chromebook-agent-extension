Create README.md that fully specifies the PoC.

Context:
- Managed Chromebooks with student/staff signed-in accounts.
- We need basic activity telemetry: navigation + downloads + heartbeat.
- Collector must run in Azure, write raw to Azure Storage Account (Blob), and ingest to Microsoft Sentinel via Azure Monitor Logs Ingestion API (DCE/DCR).
- User will manually create the Sentinel table, but we need everything else (collector, extension, infra, scripts).

Non-goals:
- No full EDR, no keystroke capture, no content inspection.
- No mTLS; HTTPS only.

Functional requirements:
Extension (MV3):
- Event types: NAVIGATION, DOWNLOAD, HEARTBEAT
- Collect url/title/time, download metadata
- Include directoryDeviceId and serialNumber where available via chrome.enterprise.deviceAttributes
- Include userEmail when available via chrome.identity
- Maintain local queue (chrome.storage.local) with at-least-once delivery and exponential backoff
- Batch size 50, flush interval 30 seconds, jitter 0-5 seconds
- Sign each batch with HMAC-SHA256 using a shared secret provisioned via managed storage policy
- Add anti-replay: X-Timestamp + max skew 5 minutes
- Include X-DeviceId (directoryDeviceId when available) and X-SessionId

Collector (Azure Functions .NET 8 isolated):
- Endpoints:
  GET /api/health -> 200 {status:"ok"}
  POST /api/v1/chrome/events/batch -> validates auth; writes to blob; ingests to Sentinel; returns 202
- Authentication:
  - Required headers: X-Timestamp, X-Signature, X-Key-Id
  - HMAC over: timestamp + '\n' + requestBodyBytes (exact bytes)
  - Secret lookup by X-Key-Id from app settings
  - Reject if timestamp older than 5 minutes or signature mismatch
- Blob layout:
  container: chrome-activity-raw
  path: yyyy/MM/dd/{keyId}/{directoryDeviceId or 'unknown'}/{hour}/events-{minute}-{guid}.jsonl
- Sentinel ingest:
  - Normalize each event into the schema expected by the custom table
  - Use Logs Ingestion API with DCE endpoint + DCR immutable id + stream name
  - On ingest failure: do not fail the request; record metrics/logs and still persist raw to blob

Infra:
- Bicep to deploy:
  - Storage account + container
  - Function App (Consumption or Premium ok; choose Consumption for PoC)
  - App Insights
  - Key settings placeholders for HMAC secrets and DCE/DCR ids
- Scripts:
  - deploy-bicep
  - create-dce-dcr (creates DCE + DCR, outputs ids)
  - packaging scripts for extension zip

Docs:
- docs/architecture.md with dataflow diagram (ASCII ok)
- docs/data-schema.md with event JSON examples and normalized record examples
- docs/operations.md with rotation procedure for HMAC keys and incident triage pivots
- docs/privacy-guardrails.md with minimization + retention guidance

Include “Quickstart” steps:
1) Deploy infra
2) Create DCE/DCR and set app settings
3) Package extension
4) Force-install via Google Admin and set managed storage (keyId + secret + collector URL)
5) Verify ingestion end-to-end
6) Run sample KQL hunts
