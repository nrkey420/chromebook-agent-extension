# Operations

## Local validation checklist
1. Configure managed storage with `collectorUrl`, `keyId`, `sharedSecret`, and optional `debug=true`.
2. Load the unpacked extension (`extension/`) in Chrome Developer Mode.
3. Generate telemetry:
   - Navigate to several pages (`NAVIGATION`)
   - Start/complete/cancel a file download (`DOWNLOAD`)
   - Wait for heartbeat interval or reload extension to force startup heartbeat (`HEARTBEAT`)
4. Open service worker logs and verify batches are signed and posted.
5. Stop collector temporarily to confirm retries (at-least-once semantics with exponential backoff), then restore collector and verify backlog drains.

## Production hardening reminder
The manifest currently allows `https://*/*` host access for PoC flexibility. Before production rollout, pin `host_permissions` to your collector URL origin.
