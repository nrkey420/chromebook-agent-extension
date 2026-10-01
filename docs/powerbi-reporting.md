# Power BI Reporting

The Power BI project lives in `reporting/powerbi/` (open `ChromebookReporting.pbip` in Power BI Desktop). It imports the
SQL views `vw_Devices`, `vw_Users` and `vw_LoginHistory` plus the `SyncState` and `IngestionErrors` tables, and has five
pages: Fleet overview, Logins, Device and User (drill-through), and Pipeline health.

Setup, publishing, refresh and who can see what: `reporting/powerbi/README.md`.
