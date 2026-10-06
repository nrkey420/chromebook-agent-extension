# Power BI Reporting

The Power BI project lives in `reporting/powerbi/` (open `ChromebookReporting.pbip` in Power BI Desktop). It imports the
SQL views `vw_Devices`, `vw_Users` and `vw_LoginHistory` plus the `SyncState` and `IngestionErrors` tables, and has
eight pages: Fleet overview, Logins, User lookup, Device lookup, IP lookup, Device and User (drill-through), and
Pipeline health.

The lookup pages answer "which devices did this user sign in to", "who signed in to this device" and "who signed in
at this IP" for a date range. Web activity and "who visited this site" are SQL procedures only, so that every look at
browsing history is audited: `docs/investigations.md`.

Setup, publishing, refresh and who can see what: `reporting/powerbi/README.md`.
