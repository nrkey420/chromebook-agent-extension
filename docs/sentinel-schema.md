# Sentinel Schema

Collector events are sent to the Log Analytics / Sentinel custom table **`ChromebookActivity_CL`** through the
Logs Ingestion API (DCE + DCR, stream **`Custom-ChromebookActivity_CL`**). Setup: `infra/scripts/create-dce-dcr.*`
(see `docs/e2e/from-zero-to-sentinel.md`, step 3).

The columns are defined once in `infra/sentinel/chromebook-activity-schema.json`, which the setup scripts use for both
the table and the DCR stream. `SentinelSchemaTests` fails the build if `PayloadNormalizer` sends a field that is not
declared (it would be silently dropped) or omits one, or if a value does not serialize as the declared type.

| Column | Type | Source |
|---|---|---|
| TimeGenerated | datetime | Event time reported by the extension (UTC) |
| EventId | string | Client-generated event GUID (de-duplication key) |
| EventType | string | NAVIGATION, DOWNLOAD, SESSION_START, LOGIN, HEARTBEAT, ... |
| EventCategory | string | ACTIVITY (navigation/download) or SESSION |
| UserEmail | string | Signed-in Google account |
| DirectoryDeviceId | string | Google directory device ID |
| DeviceSerial | string | Device serial number |
| SessionId | string | Extension session GUID |
| Url | string | Page or download URL |
| Domain | string | Host derived by the collector |
| Title | string | Page title (if collected) |
| SearchQuery | string | Search terms derived by the collector (if present) |
| DownloadFileName | string | Download file name |
| DownloadDanger | string | Chrome download danger type |
| DownloadState | string | Chrome download state |
| InternalIp | string | Device LAN IP reported by the extension |
| PublicIp | string | Client IP seen by the collector |
| MacAddress | string | Device MAC reported by the extension |
| AttributionConfidence | string | HIGH / MEDIUM / LOW |
| ExtensionVersion | string | Extension version |
| KeyId | string | HMAC key ID used to sign the batch |

## Changing the schema
1. Change `PayloadNormalizer` and `infra/sentinel/chromebook-activity-schema.json` together (tests enforce this).
2. Re-run `infra/scripts/create-dce-dcr.*` to update the table and DCR.
Adding columns is safe. Removing a column or changing its type in an existing table may require recreating the table.
