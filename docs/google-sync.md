# Google sync

Timer-triggered functions in the collector Function App that copy Google Workspace data into Azure SQL, so every
report and investigation procedure gets device inventory, student IDs and Google's own sign-in records alongside the
extension's events.

| Function | Schedule (UTC) | Google API | Writes |
|---|---|---|---|
| `GoogleDeviceSync` | every 6 h at :05 | Directory `chromeosdevices.list` (FULL, field-masked, 200/page) | `Devices` (Google columns only), `GoogleDeviceRecentUsers`, `GoogleDeviceActiveTime` (last 30 days), `IpObservations` (`Source = GOOGLE_SYNC`, one per Google check-in) |
| `GoogleUserSync` | daily 02:20 | Directory `users.list` (500/page) | `GoogleUsers` (incl. `StudentId`; no names) |
| `GoogleChromeAuditSync` | every 15 min | Reports `activities.list` app `chrome`: `CHROME_OS_LOGIN_EVENT`, `CHROME_OS_LOGOUT_EVENT`, `CHROME_OS_LOGIN_FAILURE_EVENT` | `GoogleAuditEvents` |
| `GoogleLoginAuditSync` | every 15 min (:07, :22, …) | Reports `activities.list` app `login` (all events; source IP) | `GoogleAuditEvents` |

Every run writes a row in `dbo.SyncState` (`LastRunUtc`, `LastStatus` = `SUCCESS`/`FAILED`, `ItemsProcessed`,
`LastMessage`). The audit jobs keep a watermark there; each run re-reads 3 hours before it because Google can deliver
audit events late, and duplicates are skipped. A failed run leaves the watermark unchanged, so the next run retries
the same window. The first audit run back-fills 7 days.

The extension's `Ext*` columns in `Devices` are never touched by the sync; Google's columns are never touched by the
extension. Google values win for shared inventory fields (asset ID, location, OU).

**Until it is configured the functions do nothing** (they log "skipped"): no Google calls, no SQL writes.

## One-time setup

### 1. Google Cloud: service account

1. In the [Google Cloud console](https://console.cloud.google.com/), create (or pick) a project owned by your
   Workspace organization and **enable the Admin SDK API** (APIs & Services → Library → Admin SDK API).
2. IAM & Admin → **Service accounts** → Create, e.g. `chromebook-sync`. It needs **no** Google Cloud roles.
3. Open it → **Keys** → Add key → JSON. Keep the downloaded file only until step 4 of the Azure part, then delete it.
   (If key creation is blocked by the org policy `iam.disableServiceAccountKeyCreation`, ask your Cloud admin for an
   exception on this project.)
4. Note the service account's **Unique ID** (a long number on its details page) — this is the OAuth client ID.

### 2. Google Admin: delegation and a least-privilege admin

1. Admin console → Security → Access and data control → **API controls** → **Manage domain-wide delegation** →
   Add new. Client ID: the Unique ID from above. OAuth scopes (comma-separated, exactly):

   ```
   https://www.googleapis.com/auth/admin.directory.device.chromeos.readonly,https://www.googleapis.com/auth/admin.directory.user.readonly,https://www.googleapis.com/auth/admin.reports.audit.readonly
   ```

2. Create a dedicated account for the sync to act as, e.g. `svc-chromebook-sync@district.org` (no mailbox needed,
   strong password, sign-in not used interactively).
3. Give it a **custom admin role** that can only read what the sync reads: read ChromeOS devices, read users, and
   view reports/audit logs (Admin console → Account → Admin roles → Create new role; the privilege names in the console
   change over time — pick the read-only device, user and Reports privileges). Avoid super admin.

The service account can only use the three read-only scopes above, whatever the admin's role allows.

### 3. Azure: key in Key Vault, settings, restart

```bash
KV=<keyVaultName>            # Deploy output "keyVaultName"
APP=<functionAppName>        # Deploy output "functionAppName"
RG=<resource group>

# Requires Key Vault Secrets Officer on the vault (the deploy grants it to KEYVAULT_ADMIN_OBJECT_ID / you).
az keyvault secret set --vault-name "$KV" -n GoogleServiceAccountKey --file ./chromebook-sync-key.json -o none
shred -u ./chromebook-sync-key.json   # or delete it securely
```

Then set the Google settings **through the deployment**, not in the portal (a later deploy would overwrite portal
edits):

- **GitHub Actions deploy:** add repository *variables* (Settings → Secrets and variables → Actions → Variables):
  `GOOGLE_ADMIN_EMAIL` (required), and optionally `GOOGLE_DEVICE_ORG_UNIT`, `GOOGLE_STUDENT_ID_SOURCE`. Re-run the
  **Deploy to Azure** workflow.
- **Manual deploy:** `GOOGLE_ADMIN_EMAIL=svc-chromebook-sync@district.org bash infra/scripts/deploy-bicep.sh <rg> <location>`
  (PowerShell: `-GoogleAdminEmail`).

Restart once so the Key Vault reference resolves now (otherwise it can take up to a day):

```bash
az functionapp restart -g "$RG" -n "$APP"
```

### 4. Run it once now and check

**The sync code must be deployed first.** The deploy workflow deploys `main`, so the branch with the Google sync has
to be merged (or deployed by hand) before these functions exist in Azure; until then the calls below return 404.

Then run every job once and wait for the result:

```bash
SQL_SERVER=<sqlServerFqdn> SQL_DATABASE=<sqlDatabaseName> \
  bash infra/scripts/run-google-sync.sh <resource-group> <functionAppName>
```

The script finds the app's hostname, checks that `GOOGLE_ADMIN_EMAIL` is set and the Key Vault key reference has
resolved, starts each job through the Functions admin API (master key), and, with `SQL_SERVER`/`SQL_DATABASE` set and
go-sqlcmd installed, waits for each job's row in `dbo.SyncState` and prints it. Without the SQL settings it only
starts the jobs. Run a single job by naming it: `... <resource-group> <functionAppName> GoogleDeviceSync`.

Messages you may see:

| Message | Meaning |
|---|---|
| `NOT FOUND - this function is not deployed` | Deploy the code (see above). |
| `WARNING: GOOGLE_ADMIN_EMAIL is not set` / `key reference is '…'` | The job will start but skip; finish step 3. |
| `Could not read the app's master key` | You need Contributor or Website Contributor on the Function App. |
| `Could not query SQL` | Add your IP to the SQL firewall, or sign in (`az login`) as a SQL Entra user. |

The jobs take from seconds (pilot OU) to several minutes (whole domain). Further checks once they have run:

```sql
SELECT * FROM dbo.SyncState ORDER BY SyncName;          -- every job SUCCESS with ItemsProcessed > 0

SELECT ExtensionReportingStatus, COUNT(*) AS Devices      -- NEVER_REPORTED / EXTENSION_SILENT now appear
FROM dbo.vw_Devices GROUP BY ExtensionReportingStatus;

SELECT TOP 20 * FROM dbo.vw_LoginHistory                 -- GOOGLE_CHROMEOS and GOOGLE_ACCOUNT rows
WHERE Source LIKE 'GOOGLE%' ORDER BY EventTimeUtc DESC;

SELECT COUNT(*) AS Users, COUNT(StudentId) AS WithStudentId FROM dbo.GoogleUsers;
```

## Settings

| App setting | Default | Meaning |
|---|---|---|
| `GOOGLE_SERVICE_ACCOUNT_JSON` | Key Vault reference to `GoogleServiceAccountKey` | Service account key JSON. |
| `GOOGLE_ADMIN_EMAIL` | (empty = sync off) | Admin the service account impersonates. |
| `GOOGLE_CUSTOMER_ID` | `my_customer` | Workspace customer ID; the default means "the admin's own domain". |
| `GOOGLE_DEVICE_ORG_UNIT` | (whole domain) | Limit the device sync to this OU and its children, e.g. `/Students/Pilot`. |
| `GOOGLE_USER_QUERY` | (all users) | Directory `users.list` query, e.g. `orgUnitPath='/Students'`. |
| `GOOGLE_STUDENT_ID_SOURCE` | `externalId:organization` | `externalId:organization` = the **Employee ID** field in the Admin console; `externalId:<customType>`; `externalId` (first one); `customSchema:<Schema>.<Field>`; `emailLocalPart` (when the account name *is* the ID); `none`. |
| `GOOGLE_ACTIVE_TIME_DAYS` | `30` | Days of per-device active time kept on each sync. |
| `GOOGLE_AUDIT_INITIAL_DAYS` | `7` | First audit run back-fill (max 180, Google's retention). |
| `GOOGLE_AUDIT_OVERLAP_MINUTES` | `180` | Re-read window before the watermark. |
| `GOOGLE_CHROME_EVENT_NAMES` | the three ChromeOS login/logout/failure events | Comma-separated Chrome audit event names. A name Google rejects is skipped and noted in `SyncState.LastMessage`; the others still sync. |
| `GOOGLE_LOGIN_EVENT_NAMES` | (all) | Comma-separated `login` audit event names. |

Only the first four are wired through Bicep; set the others in `main.bicep` if you need them in Azure.
Disable a single job with the app setting `AzureWebJobs.<FunctionName>.Disabled = true`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Logs say "skipped: Google sync not configured" | `GOOGLE_ADMIN_EMAIL` empty, or the Key Vault reference has not resolved (secret missing, wrong name, or app not restarted). Portal → Function App → Environment variables shows the reference status. |
| `SyncState.LastMessage` contains `unauthorized_client` | Domain-wide delegation missing, wrong client ID (use the Unique ID, not the email), or scopes not exactly as listed. Changes can take minutes to apply. |
| `Event … not found in manifest` (older version) or `Skipped event names Google does not accept` | An event name Google's API does not accept as a filter (Google's docs list `CHROME_OS_LOGIN_LOGOUT_EVENT`, but the API rejects it). Remove it from `GOOGLE_CHROME_EVENT_NAMES` / `GOOGLE_LOGIN_EVENT_NAMES`. |
| `invalid_grant` | `GOOGLE_ADMIN_EMAIL` is not a real user in the domain, or the service account key was deleted/disabled in Google Cloud. |
| `Not Authorized to access this resource/api` (403) | The impersonated admin lacks the read privilege for devices, users or reports. |
| `StudentId` empty | Check where your SIS sync writes the ID and set `GOOGLE_STUDENT_ID_SOURCE` accordingly; re-run `GoogleUserSync`. |
| Recent logins missing | Google audit data can lag (usually minutes, sometimes hours); the overlap window picks them up on later runs. |
| Device sync slow | Expected for a whole domain: ~250 pages for 50k devices. Use `GOOGLE_DEVICE_ORG_UNIT` during the pilot. |

## Security notes

- The key grants read access to every device, user and sign-in record in the domain. It lives only in Key Vault,
  readable by the Function App's managed identity; rotate it yearly (create a new key, update the secret, restart,
  delete the old key in Google Cloud).
- A keyless alternative is Google **Workload Identity Federation** trusting the Function App's Entra identity; it
  removes the stored key but needs more Google Cloud setup. Worth doing before production.
