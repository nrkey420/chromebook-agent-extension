# Chromebook reporting (Power BI)

A Power BI Project (PBIP) with the triage and helpdesk pages from `docs/plans/reporting-and-dashboards.md`:

| Page | Shows |
|---|---|
| **Fleet overview** | Device count, reporting in the last 24 h / 7 d, coverage % (devices Google saw that the extension also reported from), never-reported and silent devices, devices by OU and status, extension versions, and a work list of devices needing attention. |
| **Logins** | ChromeOS logins and extension sessions per day, users signed in, devices used, login failures, logins by hour × weekday, devices with the most users, users on the most devices, and a list of login failures. |
| **Device** (drill-through) | One device's details and everyone who signed in to it, with IPs. Right-click a serial number on another page → **Drill through → Device**. |
| **User** (drill-through) | One user's details and every sign-in, with device and IPs. Right-click a user email → **Drill through → User**. |
| **Pipeline health** | Google sync job status, collector errors per day and the latest errors. |

It uses only device, user and login data: no URLs, page titles, searches or downloads. Web activity stays in the
audited investigation procedures (`docs/investigations.md`).

## Files

```
reporting/powerbi/
├── ChromebookReporting.pbip                 open this in Power BI Desktop
├── ChromebookReporting.SemanticModel/       tables, relationships, measures (TMDL)
└── ChromebookReporting.Report/              pages and visuals (PBIR JSON)
```

| Model table | Source | Rows loaded |
|---|---|---|
| Devices | `dbo.vw_Devices` | all |
| Users | `dbo.vw_Users` | all |
| Logins | `dbo.vw_LoginHistory` | last `LoginHistoryDays` days (default 90), filtered in SQL |
| Calendar | generated | the same days |
| SyncState | `dbo.SyncState` | all |
| IngestionErrors | `dbo.IngestionErrors` | last 30 days |

## Open it

1. Install or update **Power BI Desktop** (a release from September 2025 or later; it must support PBIR and TMDL
   projects, which current releases do by default).
2. Make sure your IP is allowed through the Azure SQL firewall and that you are in the SQL Entra admin group or the
   `ChromebookDeviceReaders` role (`docs/sql-access.md`).
3. **File → Open** → `reporting/powerbi/ChromebookReporting.pbip`. The report opens without data the first time.
4. **Home → Transform data → Edit parameters**: set `SqlServer` (deployment output `sqlServerFqdn`) and `SqlDatabase`
   (`sqlDatabaseName`). Optionally change `LoginHistoryDays`.
5. **Refresh**. When asked for credentials, choose **Microsoft account** and sign in with your Entra account.
6. **File → Save** writes your changes back to these files. Commit them like code.

Power BI Desktop owns the files once you have opened and saved them. Edit pages and measures in Desktop; the CI tests
below keep checking whatever you commit.

## Publish and share

The model imports data, so **everyone who can open the published report sees all the data it loaded**, whatever their
own SQL permissions. That is fine for this data (devices, users, logins) as long as the audience matches the
`ChromebookDeviceReaders` group:

1. Publish to a workspace whose members/viewers are your helpdesk, ops and IR staff (not a broad audience).
2. In the semantic model's settings (Power BI service): **Data source credentials** → OAuth2, signed in as an account
   that is in `ChromebookDeviceReaders` (a dedicated service account is best). Azure SQL already allows Azure services
   through its firewall.
3. **Scheduled refresh**: several times a day (for example 07:00, 10:00, 13:00, 16:00). "Last 24 h" measures are
   relative to when you view the report, so stale data understates reporting devices; refresh at least daily.

Per-school access (a principal sees only their school) needs row-level security, not built yet.

## What has been checked

- The model was generated with Microsoft's Tabular Object Model and parses with its TMDL parser.
- Every report file validates against Microsoft's published PBIR JSON schemas.
- CI tests (`collector/tests/.../PowerBiProjectTests.cs`) check on every PR that:
  - the model parses;
  - every field a visual or filter uses exists in the model as the right kind (column or measure);
  - every column and measure a measure formula references exists;
  - every column the model reads exists in its SQL view (against the real schema scripts on SQL Server);
  - an account with only `ChromebookDeviceReaders` can read all of it, so refresh works with least privilege.

Not yet checked: opening it in Power BI Desktop. DAX formulas and Power Query steps are only fully evaluated by Power
BI. If Desktop reports an error on first open or refresh, the message names the table, measure or visual to fix.

## Not included yet

- **Web activity page** (top domains, new domains, dangerous downloads): needs summary tables first, so that no
  dashboard has bulk, unaudited access to individual URLs. See `docs/sql-access.md`, "Not covered yet".
- **Per-school row-level security.**
- **DirectQuery** for the drill-through pages at 50k-device scale (`docs/plans/reporting-and-dashboards.md`, section 4).
