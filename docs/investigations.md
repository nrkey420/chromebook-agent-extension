# Investigations: pulling activity for a case

Investigation procedures live in Azure SQL (`collector/src/ChromeCollector.FunctionApp/Sql/003_procedures.sql`).
Connect with your Entra account (SSMS, Azure Data Studio, or VS Code with the `mssql` extension) to the database
from the deployment output `sqlDatabaseName`. You need to be in the investigators group (`docs/sql-access.md`); the
sign-in lookups also work for the device readers group, and the audit-review queries below need the audit reviewers
group.

**Every run is recorded** in `dbo.InvestigationAudit`: who ran it (your sign-in), when, the case number, the filters,
and for the lookups and web activity the number of rows returned. Always pass the ticket/case number.

Times you enter are **local time** (`ReportingSettings.ReportingTimeZone`, Eastern by default) unless you add
`@TimesAreUtc = 1`. Results include both UTC and local columns.

## Which procedure

| Question | Procedure | Who can run it |
|---|---|---|
| Which devices did this user sign in to? | `EXEC dbo.usp_UserDevices @User = '123456', @From = '2026-09-01', @To = '2026-10-01';` | device readers, investigators |
| Who signed in to this device? | `EXEC dbo.usp_DeviceUsers @Device = '5CD1234XYZ', @From = '2026-09-01', @To = '2026-10-01';` | device readers, investigators |
| Which device had this IP? | `EXEC dbo.usp_IpLookup @Ip = '10.20.30.40', @From = '2026-10-01 08:00', @To = '2026-10-01 12:00', @CaseNumber = 'IR-2026-0142';` | investigators |
| What did this user (or device) browse? | `EXEC dbo.usp_WebActivity @User = '123456', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';` | investigators |
| Who went to this site? | `EXEC dbo.usp_SiteVisitors @Domain = 'example.com', @From = '2026-09-01', @To = '2026-10-01', @CaseNumber = 'IR-2026-0142';` | investigators |
| Which device is this? (serial, asset tag, MAC, IP, last user) | `EXEC dbo.usp_FindDevice @Search = '5CD1234XYZ', @CaseNumber = 'IR-2026-0142';` | device readers, investigators |
| Who was on this IP at exactly 09:15 (± 30 min)? | `EXEC dbo.usp_WhoWasOnIp @Ip = '10.20.30.40', @At = '2026-10-01 09:15', @CaseNumber = 'IR-2026-0142';` | investigators |
| Everything that happened on a device | `EXEC dbo.usp_DeviceTimeline @Device = '5CD1234XYZ', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';` | investigators |
| Everything a student did | `EXEC dbo.usp_UserTimeline @User = '123456', @From = '2026-09-28', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';` | investigators |

The first five share these rules:

- **Dates are optional.** Leave out `@From` and `@To` for the last 30 days up to now. `@From` is inclusive and `@To`
  exclusive, so `@From = '2026-10-01', @To = '2026-10-02'` is exactly October 1. A time is optional too
  (`'2026-10-01 08:00'`).
- **`@User`** is an email address or student ID. **`@Device`** is a serial number, asset tag or directory device ID.
- **`@CaseNumber`** is required for the two that return web content (`usp_WebActivity`, `usp_SiteVisitors`) and
  recorded whenever you give it on the others. Give it every time you work a case.
- The sign-in lookups (`usp_UserDevices`, `usp_DeviceUsers`, `usp_IpLookup`) are also in Power BI as the
  **User lookup**, **Device lookup** and **IP lookup** pages, for people who prefer clicking (`reporting/powerbi/README.md`).
  Web activity and site visitors are only here, so that every look at browsing history is audited.

## Devices a user signed in to / users who signed in to a device

```sql
-- Every Chromebook student 123456 used in September:
EXEC dbo.usp_UserDevices @User = '123456', @From = '2026-09-01', @To = '2026-10-01';

-- Everyone who signed in to the Chromebook with asset tag A-100 in the last 30 days:
EXEC dbo.usp_DeviceUsers @Device = 'A-100';
```

**Result 1** — one row per device (`usp_UserDevices`) or per user (`usp_DeviceUsers`), most recent first: serial,
asset tag, location / student ID and OU, first and last sign-in, number of Google logins, extension sessions and
failed logins, the last IPs seen, and **Evidence**:

| Evidence | Meaning |
|---|---|
| `GOOGLE_AND_EXTENSION` | Both Google and the extension saw the sign-ins. Web activity is available. |
| `GOOGLE_ONLY` | Google saw the user sign in but the extension never reported. **There is no web activity for them on this device.** Usually the extension is not force-installed for that user's OU; check `chrome://extensions` and `chrome://policy` while signed in as them. |
| `EXTENSION_ONLY` | Only the extension saw it (Google's audit log lags by minutes to hours, or the device is outside the Google sync's OU). |
| `FAILURES_ONLY` | Only failed sign-in attempts. |

**Result 2** — every sign-in behind result 1, oldest first, with source, user, device and IPs.

Sign-ins come from Google's ChromeOS login events and from the extension's sessions. A session that was already
open when the window starts is included, so a first sign-in can be slightly earlier than `@From`. A row with no
`UserEmail` in `usp_DeviceUsers` is an extension session whose user the extension could not identify.

## Which device had an IP address

```sql
-- A firewall alert names 10.20.30.40 between 8 and noon:
EXEC dbo.usp_IpLookup @Ip = '10.20.30.40', @From = '2026-10-01 08:00', @To = '2026-10-01 12:00', @CaseNumber = 'IR-2026-0142';

-- Everything on one subnet that day:
EXEC dbo.usp_IpLookup @Ip = '10.20.30.*', @From = '2026-10-01', @To = '2026-10-02', @CaseNumber = 'IR-2026-0142';
```

`@Ip` matches the device's internal (LAN) address and its public (WAN) address. End it with `*` for everything
starting with that prefix (`10.20.30.*`, at least 4 characters before the `*`). A school's public IP is shared by
every device behind it, so a public IP usually returns many devices: use the internal IP to pin down one.

**Result 1** — one row per IP, device and user: matched on `INTERNAL` or `PUBLIC`, the IP, serial, asset tag,
location, user, student ID, how the user was known (`EXTENSION`, or `INFERRED_FROM_GOOGLE_LOGIN` = whoever last
signed in on that device according to Google), first and last seen, number of observations.
**Result 2** — Google sign-ins (account and ChromeOS) reported from that IP.

`usp_WhoWasOnIp` remains for "who had it at exactly this moment": it sorts by how close each sighting was to `@At`.

## Who visited a site

```sql
-- Everyone who went to example.com or any of its subdomains this month:
EXEC dbo.usp_SiteVisitors @Domain = 'example.com', @From = '2026-10-01', @To = '2026-11-01', @CaseNumber = 'IR-2026-0142';

-- A pasted link works; narrow to one page or video with @UrlContains, and to the exact host with @IncludeSubdomains = 0:
EXEC dbo.usp_SiteVisitors @Domain = 'https://www.example.com/watch?v=abc123', @UrlContains = 'v=abc123',
     @IncludeSubdomains = 0, @CaseNumber = 'IR-2026-0142';
```

| Parameter | Notes |
|---|---|
| `@Domain` | `example.com`, or a pasted URL (the scheme, `www.`, port and path are dropped). Matches subdomains (`mail.example.com`), never lookalikes (`notexample.com`). |
| `@UrlContains` | Optional text the URL must contain (a path, a video ID). |
| `@IncludeSubdomains` | Default 1. |
| `@IncludeDownloads` | Default 1: downloads from the site count too. |
| `@MaxRows` | Default 50,000, as for `usp_WebActivity`. |
| `@CaseNumber` | **Required.** |

**Result 1** — one row per user, most visits first: student ID, OU, visits, downloads, number of devices, number of
distinct (sub)domains, first and last visit. Covers everything that matched, even if result 2 was truncated.
**Result 2** — every visit, oldest first: time, user, device, URL, title, IPs, `Truncated`, case number.

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
| `@From`, `@To` | Window start (inclusive) and end (exclusive), so `@From = '2026-10-01', @To = '2026-10-02'` is exactly October 1. Leave both out for the last 30 days. |
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

The audit only proves something while investigators cannot edit it and cannot read web activity directly. Give
analysts access through the `ChromebookInvestigators` role and audit reviewers through `ChromebookAuditReviewers`
(`docs/sql-access.md`), never through the SQL admin group.
