# Architecture

## Data flow
1. Chromebook extension captures browser activity events (`NAVIGATION`, `DOWNLOAD`, `HEARTBEAT`).
2. Events are queued in local extension storage and flushed in batches.
3. Batch is signed with HMAC and posted to Azure Function collector.
4. Collector validates signature and timestamp, then:
   - writes raw payload as JSONL in Blob Storage
   - forwards normalized records to Microsoft Sentinel Logs Ingestion API.

## Components
- `extension/`: MV3 service worker implementation.
- `collector/`: .NET 8 isolated Azure Functions collector and Sentinel client.
- `infra/`: Bicep templates and deployment scripts.
- `.github/workflows/`: CI/CD automation.

## Security controls
- Shared-secret HMAC between extension and collector.
- Timestamp replay window check (default 5 minutes).
- Managed identity auth from Function App to Logs Ingestion API.
- No secrets committed; templates and GitHub secrets only.
