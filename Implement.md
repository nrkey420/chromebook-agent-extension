You are Codex working inside this repo. Follow these rules:

- Read README.md and docs/architecture.md first. Ask no questions unless blocked.
- Implement in milestones. After each milestone:
  1) run unit tests
  2) run a basic local integration test (curl) against the Function host
  3) update docs that changed
- Keep diffs scoped. Do not introduce unrelated frameworks.
- All secrets must be in local.settings.json.template or GitHub Actions secrets, never committed.
- Deliverables must be runnable on Windows (PowerShell) and Linux/macOS (bash).
- Add clear instructions for Google Admin force-install policy and managed storage config keys.

Milestones:
M1 Repo scaffolding + docs skeleton
M2 Chrome extension (MV3) with HMAC signing + queue + retry
M3 Azure Function collector: validate HMAC, write to Blob as JSONL, return 202
M4 Sentinel ingest client: send normalized events to Logs Ingestion API (DCE/DCR)
M5 Infra-as-code (Bicep) + scripts (deploy, create DCE/DCR) + GitHub Actions
M6 End-to-end PoC guide: “from zero to telemetry in Sentinel” + sample KQL hunts
