# chromebook-session-attribution-poc
Azure-native PoC for Chromebook session attribution, browser activity telemetry, Sentinel hunting, Azure SQL reporting, and Power BI DirectQuery views.

## Architecture
Chrome Extension (MV3) → Azure Function collector (.NET 8 isolated) → Blob raw JSONL + Azure SQL + Sentinel Logs Ingestion.

## Quickstart
1. Deploy infra: `infra/scripts/deploy-bicep.sh`.
2. Create DCE/DCR: `infra/scripts/create-dce-dcr.sh <workspaceResourceId>`.
3. Initialize SQL schema: `infra/scripts/init-sql.sh`.
4. Set Function app settings (`HMAC_KEYS__KEY1`, SQL and Sentinel values).
5. Package extension: `extension/tools/pack.sh`.
6. Force install extension and managed policy (see `infra/scripts/set-extension-policy.md`).
7. Verify blob/SQL/Sentinel with `infra/scripts/send-test-batch.sh`.
8. Connect Power BI DirectQuery to SQL views (`vw_*`).

## Prereqs
Azure CLI, .NET 8 SDK, sqlcmd, Chrome Enterprise managed environment.

## Repo structure
- `extension/` MV3 service worker extension.
- `collector/` Azure Function collector + SQL scripts + tests.
- `infra/` Bicep + deployment scripts.
- `docs/` architecture, data model, privacy, operations, Sentinel, Power BI.
