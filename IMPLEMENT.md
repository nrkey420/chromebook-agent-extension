# Implementation Plan

## Milestones

### M1 Repo scaffolding + docs skeleton
- Create top-level folders for extension, collector, infra, and CI.
- Add architecture and end-to-end runbook docs.

### M2 Chrome extension (MV3) with HMAC signing + queue + retry
- Managed config loading via `chrome.storage.managed`.
- Capture `NAVIGATION`, `DOWNLOAD`, `HEARTBEAT` events.
- Queue in `chrome.storage.local` with periodic flush.
- HMAC-SHA256 signing over `timestamp + '\n' + body`.

### M3 Azure Function collector
- .NET 8 isolated worker HTTP endpoint for event batches.
- Validate HMAC headers and timestamp skew.
- Store received events as JSONL in Azure Blob Storage.
- Return `202 Accepted` on successful enqueue/persist.

### M4 Sentinel ingestion client
- Normalize extension payload to Sentinel custom table schema.
- Send records through Logs Ingestion API using DCE/DCR stream.
- Use managed identity (`DefaultAzureCredential`) for auth.

### M5 IaC + scripts + GitHub Actions
- Bicep for storage account, function app, plan, app insights.
- Scripts for deployment and DCE/DCR stream setup.
- CI workflow for extension + collector build.
- CD workflow for infra + function deployment.

### M6 End-to-end guide + verification
- Document Google Admin force-install and managed policy values.
- Document deployment, validation, and Sentinel KQL hunts.
