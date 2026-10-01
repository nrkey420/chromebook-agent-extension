# Reporting and dashboards for triage and incident response

Two audiences, two tools, one data contract:

| Audience | Questions | Tool | Source |
|---|---|---|---|
| Ops / helpdesk / leadership | How many devices? Are they reporting? How many logins, where, when? | **Power BI** (DirectQuery or Import) | SQL views `vw_*` |
| Security / IR analysts | Who was on this device or IP at 09:15? What did this user do between X and Y? Pull web activity for a case. | **SQL procedures** (`usp_*`) for case work, **Sentinel workbook + KQL** for hunting and alerting | SQL + `ChromebookActivity_CL` |

The SQL views stay the reporting contract (per `IMPLEMENT.md`). Dashboards never query base tables directly.

## 1. Prerequisite: the Google sync

**Status: built** (`docs/google-sync.md`); it only needs the one-time Google setup. Design notes kept below.

Several numbers the IR team will ask for come from Google, not the extension. Build a timer-triggered Function
(same app, managed identity, a Google service account with domain-wide delegation, read-only scopes) that fills the
tables already defined in `001_tables.sql`:

| Job | Google API | Writes | Schedule |
|---|---|---|---|
| Device inventory | Admin SDK Directory `chromeosdevices.list` (projection FULL, paged) | `Devices` (Google columns), `GoogleDeviceRecentUsers`, `GoogleDeviceActiveTime` | Every 6 h (50k devices ≈ 250 pages of 200) |
| Users | Directory `users.list` | `GoogleUsers` (incl. `StudentId` from your custom schema / externalIds) | Daily |
| ChromeOS logins | Reports API `activities.list` application `chrome` (login, logout, login failure) | `GoogleAuditEvents` | Every 15 min, from `SyncState.WatermarkUtc` minus an overlap (Reports data can arrive late; the unique index ignores duplicates) |
| Account sign-ins | Reports API application `login` | `GoogleAuditEvents` | Every 15 min |

Without this job the dashboards below still work, but: device totals = devices the extension has seen, no
"silent device" detection, no login failures, no student IDs.

## 2. Power BI report

One `.pbix` with these pages. Every page has slicers for date range, school (device OU / annotated location) and
user OU. Filter on the **UTC** columns (`EventTimeUtc`, `ObservedUtc`) in the model — the `…Local` columns are
computed by a function per row and cannot use indexes.

### Page 1 — Fleet overview ("number of devices")

| Visual | Measure / field | Source |
|---|---|---|
| KPI cards | Devices in inventory · reporting last 24 h · reporting last 7 d · never reported · silent (Google active but extension quiet) | `vw_Devices` (`ExtLastSeenUtc`, `ExtensionReportingStatus`) |
| Coverage % | reporting last 24 h ÷ Google-active last 24 h | `vw_Devices` |
| Bar | Devices by school / OU, split by status | `vw_Devices` |
| Bar | Extension version distribution | `vw_Devices.ExtVersion` |
| Bar | ChromeOS version, AUE date (`AutoUpdateThrough`) | `vw_Devices` |
| Table | Silent / never-reported devices with serial, asset, location, last user | `vw_Devices` — the helpdesk work list |

```sql
SELECT
  COUNT(*)                                                                         AS DevicesInInventory,
  SUM(CASE WHEN ExtLastSeenUtc >= DATEADD(hour, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS ReportingLast24h,
  SUM(CASE WHEN ExtLastSeenUtc >= DATEADD(day, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END)   AS ReportingLast7d,
  SUM(CASE WHEN ExtensionReportingStatus = 'NEVER_REPORTED' THEN 1 ELSE 0 END)     AS NeverReported,
  SUM(CASE WHEN ExtensionReportingStatus = 'EXTENSION_SILENT' THEN 1 ELSE 0 END)   AS Silent
FROM dbo.vw_Devices;
```

### Page 2 — Login activity ("device logins")

| Visual | Measure | Source |
|---|---|---|
| Line | Logins per day (extension `SESSION_START`, Google `LOGIN`) | `vw_LoginHistory` |
| Heatmap | Logins by hour × weekday (spot after-hours use) | `vw_LoginHistory` |
| KPI | Unique users, unique devices, avg users per device | `vw_LoginHistory` |
| Table | Login failures by user/device (Google `LOGIN_FAILURE`) — brute-force / shared-password signal | `vw_LoginHistory` |
| Table | Most-shared devices (distinct users per device per day) | `vw_LoginHistory` |
| Table | Users on many devices in one day | `vw_LoginHistory` |

```sql
SELECT CAST(EventTimeLocal AS date) AS Day, Source,
       COUNT(*) AS Logins, COUNT(DISTINCT UserEmail) AS Users, COUNT(DISTINCT DirectoryDeviceId) AS Devices
FROM dbo.vw_LoginHistory
WHERE LoginEvent IN ('LOGIN', 'SESSION_START')
  AND EventTimeUtc >= DATEADD(day, -30, SYSUTCDATETIME())
GROUP BY CAST(EventTimeLocal AS date), Source
ORDER BY Day;
```

### Page 3 — Device drill-through ("logins to a specific device")

Drill-through target from any device row, or a search box on serial / asset ID / MAC / IP. Shows:
1. Device card (serial, asset, location, OU, model, last user, last IPs, reporting status).
2. **Who logged in, when, from which IP** — `vw_LoginHistory` filtered to the device, newest first, with session
   start, last seen (real end time, see test S2), and internal/public IP.
3. Timeline of activity on the device (`vw_InvestigationTimeline`).
4. IP history (`vw_IpHistory`).

Same data from SQL for a case file (already exists):

```sql
EXEC dbo.usp_FindDevice @Search = '5CD1234XYZ';          -- serial, asset, MAC, IP or last user
EXEC dbo.usp_DeviceTimeline @Device = '5CD1234XYZ', @From = '2026-09-28', @To = '2026-10-02';
-- result 2 = logins to the device; times are local unless @TimesAreUtc = 1
```

### Page 4 — User drill-through

Profile, devices used (from `Sessions`), logins, searches, downloads, top domains. SQL equivalent:
`EXEC dbo.usp_UserTimeline @User = '<email or student ID>', @From = …, @To = …;`

### Page 5 — Web activity

| Visual | Source |
|---|---|
| Top domains (by visits and by users) for the filter context | `vw_WebActivity` |
| New domains (first seen in the last 24 h across the fleet) | `vw_WebActivity` / rollup |
| Searches table (watch-list terms for safeguarding, if your policy allows) | `vw_SearchActivity` |
| Downloads with `DownloadDanger` ≠ `safe` | `vw_Downloads` |

At 50k devices this page must read a **daily rollup table**, not raw events (see section 4).

### Page 6 — Pipeline health (ops)

Events/min, accepted vs rejected batches, `IngestionErrors` by layer, Sentinel vs SQL daily counts, Google sync
`SyncState` (last run, status, lag), heartbeats reporting `queue_full_dropped`. Also an Azure Monitor workbook
over App Insights for latency and 4xx/5xx — alert on 5xx > 1 % for 5 min, Sentinel ingest errors, sync lag > 1 h.

## 3. Pulling web activity for an incident

**Status: built.** `dbo.usp_WebActivity` and the `dbo.InvestigationAudit` table are in the schema scripts, and the
older procedures (`usp_DeviceTimeline`, `usp_UserTimeline`, `usp_WhoWasOnIp`, `usp_FindDevice`) now log every run
too (with an optional `@CaseNumber`). Analyst runbook: `docs/investigations.md`.

Differences from the first sketch in this plan: the window is `[@From, @To)` so whole days don't overlap; a
`@MaxRows` cap (default 50,000) flags truncation on every row; a second result set summarises the same filter by
domain; the audit row records the resolved user and the number of rows returned.

For windows older than the SQL hot retention (section 4), run the same filter in Log Analytics or over the raw
archive:

```kql
// Web activity for a user or device (Sentinel / Log Analytics)
let from = datetime(2026-09-28T00:00:00Z);
let to   = datetime(2026-10-02T00:00:00Z);
ChromebookActivity_CL
| where TimeGenerated between (from .. to)
| where UserEmail == "123456@district.org" or DeviceSerial == "5CD1234XYZ"
| where EventType in ("NAVIGATION", "DOWNLOAD")
| project TimeGenerated, EventType, UserEmail, DeviceSerial, Domain, Url, Title, SearchQuery,
          DownloadFileName, DownloadDanger, InternalIp, PublicIp, SessionId, EventId
| order by TimeGenerated asc
```

```kql
// Device counts and logins (hunting / workbook tiles)
ChromebookActivity_CL
| where TimeGenerated > ago(24h)
| summarize Devices = dcount(DirectoryDeviceId), Users = dcount(UserEmail),
            Logins = countif(EventType == "LOGIN")

// Logins to one device
ChromebookActivity_CL
| where TimeGenerated > ago(7d) and DeviceSerial == "5CD1234XYZ"
| where EventType in ("SESSION_START", "LOGIN", "SESSION_END", "LOGOUT")
| project TimeGenerated, EventType, UserEmail, InternalIp, PublicIp, SessionId
| order by TimeGenerated asc
```

Package these as a **Sentinel workbook** ("Chromebook investigation") with parameters for user, device, IP and
time range, plus analytics rules for: dangerous downloads, login failures above a threshold, a user on more than
N devices in an hour, extension silent on many devices at once (possible tampering or outage).

## 4. Keeping reports fast at 50k devices

- **Rollup tables**, refreshed every 15 min by a SQL Agent–style job (Elastic Job or a timer Function):
  `DeviceDailySummary` (device, day, first/last seen, users, events by type, active minutes) and
  `DomainDailySummary` (day, domain, OU, visits, distinct users). Fleet, login trend and top-domain visuals read
  these; only drill-throughs touch raw events.
- **Power BI mode:** Import (scheduled refresh) for pages 1, 2, 5 and 6 on rollups; DirectQuery only for the
  device/user drill-through pages, which always carry a device/user filter.
- **Partition `ActivityEvents` by day** with a clustered columnstore for older partitions, and drop old
  partitions per retention instead of `DELETE` (see the scale plan).

## 5. Access, privacy and evidence handling

Web activity of students is sensitive (FERPA and state student-privacy law). Before production:

- **Roles** (Entra groups → SQL database roles → Power BI workspace roles):
  - *Helpdesk*: pages 1–2 and device card only; no URLs, titles or searches.
  - *Principal / safeguarding*: own school only (SQL row-level security on device/user OU).
  - *IR analyst*: everything, via `usp_*` procedures; `EXECUTE` on procedures, no direct `SELECT` on base tables.
- **Audit:** `InvestigationAudit` for every pull, plus Azure SQL auditing to Log Analytics; review monthly.
- **Case export:** export the procedure result to CSV, record the file's SHA-256 in the ticket with the case number,
  analyst and parameters, and store it in the case evidence location (not email).
- **Retention:** agree with legal/records before go-live, e.g. web activity 90 days in SQL / 1 year in archive;
  logins, sessions and IP observations 1–3 years. Enforce with partition drops, Log Analytics table retention and
  Blob lifecycle rules.

## 6. Mock investigations (IR readiness exercise)

Run each on the pilot with a timer; target < 15 minutes each:
1. "Abusive message sent from IP 10.20.30.40 at 09:15 on Tuesday — who was it?" → `usp_WhoWasOnIp`, then `usp_DeviceTimeline`.
2. "Device 5CD1234XYZ was damaged in room 204 — who used it this week?" → page 3 / `usp_DeviceTimeline`.
3. "Student 123456 may be at risk — pull their web activity for the last 3 days." → `usp_WebActivity` with a case number, export, hash.

## 7. Build order

1. ~~Google sync job~~ — built; complete the Google setup in `docs/google-sync.md`.
2. ~~`InvestigationAudit` table + `usp_WebActivity` + audit inserts in the existing procedures~~ — built.
3. Power BI pages 3 and 1 (device drill-through and fleet overview), then 2, 4, 5, 6.
4. Sentinel workbook and analytics rules.
5. Rollup tables and switch fleet pages to them before passing 5k devices.
6. Roles, RLS and auditing before any non-pilot user gets access.
7. Fix `docs/powerbi-reporting.md` to list the real views.
