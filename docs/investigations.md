# Investigations: pulling activity for a case

Investigation procedures live in Azure SQL (`collector/src/ChromeCollector.FunctionApp/Sql/003_procedures.sql`).
Connect with your Entra account (SSMS, Azure Data Studio, or VS Code with the `mssql` extension) to the database
from the deployment output `sqlDatabaseName`.

**Every run is recorded** in `dbo.InvestigationAudit`: who ran it (your sign-in), when, the case number, the filters,
and for web activity the number of rows returned. Always pass the ticket/case number.

Times you enter are **local time** (`ReportingSettings.ReportingTimeZone`, Eastern by default) unless you add
`@TimesAreUtc = 1`. Results include both UTC and local columns.

## Which procedure

| Question | Procedure |
|---|---|
| Which device is this? (serial, asset tag, MAC, IP, last user) | `EXEC dbo.usp_FindDevice @Search = '5CD1234XYZ', @CaseNumber = 'IR-2026-0142';` |
| Who was on this IP at 09:15? | `EXEC dbo.usp_WhoWasOnIp @Ip = '10.20.30.40', @At = '2026-10-01 09:15', @CaseNumber = 'IR-2026-0142';` |
| Who used this device, and what happened on it? | `EXEC dbo.usp_DeviceTimeline @Device = '5CD1234XYZ', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';` |
| Everything a student did | `EXEC dbo.usp_UserTimeline @User = '123456', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';` |
| **Web activity for a case file** | `EXEC dbo.usp_WebActivity …` (below) |

## usp_WebActivity

Page visits, searches and downloads for a device and/or a user over a time window.

```sql
-- A student's web activity on any device, Monday through Thursday (local time):
EXEC dbo.usp_WebActivity @User = '123456', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';

-- Everything done on one Chromebook (any user) during 3rd period:
EXEC dbo.usp_WebActivity @Device = '5CD1234XYZ', @From = '2026-10-01 10:05', @To = '2026-10-01 10:55', @CaseNumber = 'IR-2026-0142';

-- One student on one device, only a particular site and its subdomains, without downloads:
EXEC dbo.usp_WebActivity @User = '123456@district.org', @Device = 'A-100', @From = '2026-10-01', @To = '2026-10-02',
     @Domain = 'example.com', @IncludeDownloads = 0, @CaseNumber = 'IR-2026-0142';
```

| Parameter | Notes |
|---|---|
| `@User` | Email address or student ID. |
| `@Device` | Serial number, asset tag or directory device ID. Give `@User`, `@Device` or both (both = that user on that device). |
| `@From`, `@To` | Window start (inclusive) and end (exclusive), so `@From = '2026-10-01', @To = '2026-10-02'` is exactly October 1. |
| `@CaseNumber` | **Required.** |
| `@Domain` | Optional. `example.com` matches `example.com`, `www.example.com` and `mail.example.com`, not `notexample.com`. |
| `@IncludeDownloads` | Default 1. |
| `@MaxRows` | Default 50,000. If more rows matched, only the first `@MaxRows` are returned and `Truncated = 1` on every row: narrow the window or add filters. |

**Result 1** — one row per visit or download, oldest first: time (UTC and local), user, student ID, device serial,
asset tag and location, domain, URL, page title, search engine and search terms, download file/type/danger/state,
internal and public IP, session ID, event ID, and the case number.

**Result 2** — per-domain summary of everything that matched (even if result 1 was truncated): visits, searches,
downloads, first and last seen. Useful for a quick read before going through result 1.

The procedure reads the SQL copy of web activity. Anything older than SQL retention must be pulled from Sentinel
(`ChromebookActivity_CL`) — see `docs/plans/reporting-and-dashboards.md`, section 3, for the equivalent KQL.

## Saving evidence

1. Run the procedure with the case number.
2. Save result 1 (and result 2 if useful) as CSV: right-click the results grid → **Save as CSV** (SSMS, Azure Data
   Studio and VS Code all have this).
3. Hash each file and record the hash in the ticket with the case number, your name and the exact command you ran:
   - Windows: `Get-FileHash .\IR-2026-0142-web.csv -Algorithm SHA256`
   - macOS / Linux: `shasum -a 256 IR-2026-0142-web.csv`
4. Store the files in the case evidence location, not email or a personal drive.

## Reviewing the audit trail

```sql
-- Everything pulled for a case
SELECT RunUtc, dbo.fn_ToLocal(RunUtc) AS RunLocal, RunBy, ProcedureName, Parameters, RowsReturned
FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-2026-0142' ORDER BY RunUtc;

-- Monthly review: pulls without a case number, and the busiest users of the procedures
SELECT RunBy, ProcedureName, COUNT(*) AS Runs, SUM(CASE WHEN CaseNumber IS NULL THEN 1 ELSE 0 END) AS WithoutCase
FROM dbo.InvestigationAudit WHERE RunUtc >= DATEADD(month, -1, SYSUTCDATETIME())
GROUP BY RunBy, ProcedureName ORDER BY Runs DESC;
```

The audit only proves something while investigators cannot edit it and cannot read the tables directly. Until the
roles in `docs/plans/reporting-and-dashboards.md` (section 5) are in place, anyone with direct table access can
query around the procedures — set those roles up before giving analysts access.
