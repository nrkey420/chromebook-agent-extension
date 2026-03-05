# Privacy Guardrails

## Data minimization
The extension is intentionally scoped to collect only operational browsing metadata:
- Navigation: URL, optional title, and event timestamp.
- Downloads: download metadata (for example URL, filename, MIME type, danger/state transitions, and timestamps).
- Heartbeat: extension operational metadata.

The extension does **not** collect form entries, keystrokes, page body/content, cookies, or other in-page user input.

A managed policy flag `collectTitles` (default `true`) allows organizations to disable title collection when stricter minimization is required.

## Collector controls
The collector enforces additional controls:
- Request body size cap (1 MB).
- Required schema fields for each event (`EventType`, `EventTime`).
- In-memory token-bucket rate limit by `keyId` (PoC control).
- Correlation-focused logging with minimal metadata only.
- Shared secrets and request signatures are never written to logs.

For production, front the collector with API Management (or equivalent gateway/WAF) for durable, distributed throttling and policy enforcement.

## Recommended retention
Use the shortest retention period that satisfies operational and security requirements. A typical baseline is:
- **30–90 days** for raw and normalized telemetry.
- Longer retention only when required by formal compliance/legal obligations.

Apply documented purge policies and access controls to all telemetry storage locations.
