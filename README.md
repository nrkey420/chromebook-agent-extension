# chromebook-session-attribution-poc
Azure-native PoC for Chromebook session attribution, browser activity telemetry, Sentinel hunting, Azure SQL reporting, and Power BI DirectQuery views.

## Architecture
Chrome Extension (MV3) → Azure Function collector (.NET 8 isolated) → Blob raw JSONL + Azure SQL + Sentinel Logs Ingestion.

## Quickstart
1. Deploy infra: `infra/scripts/deploy-bicep.sh`.
2. Create DCE/DCR: `infra/scripts/create-dce-dcr.sh <workspaceResourceId>`.
3. Initialize SQL schema and grant the Function App's managed identity access: `infra/scripts/init-sql.sh`.
4. Function app settings are set by the deployment (Flex Consumption; storage, SQL and Key Vault via managed identity). Sentinel settings are optional.
5. Package extension: `extension/tools/pack.sh`.
6. Force install extension and managed policy (see `infra/scripts/set-extension-policy.md`).
7. Verify blob/SQL/Sentinel with `infra/scripts/send-test-batch.sh`.
8. Connect Power BI DirectQuery to SQL views (`vw_*`).

## Continuous deployment
Pushes to `main` that change `collector/` or `infra/` deploy to Azure via GitHub Actions (`.github/workflows/deploy.yml`). One-time setup: `docs/github-actions-deploy.md`.

## Prereqs
Azure CLI, .NET 8 SDK, go-sqlcmd (Azure SQL uses Microsoft Entra-only auth), Chrome Enterprise managed environment.

## Repo structure
- `extension/` MV3 service worker extension.
- `collector/` Azure Function collector + SQL scripts + tests.
- `infra/` Bicep + deployment scripts.
- `docs/` architecture, data model, privacy, operations, Sentinel, Power BI.
