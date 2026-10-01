# Architecture
```text
Chrome Extension -> Function Collector -> Blob(raw) + SQL(normalized) + Sentinel(hunting)
                                         -> Power BI (DirectQuery on SQL views)
Google Admin SDK -> Google sync timers  -> SQL (device inventory, users, ChromeOS/account sign-ins)
```
Blob keeps immutable raw events, SQL stores attribution/reporting records, Sentinel stores hunting-friendly normalized security records.
Google sync (docs/google-sync.md) adds Google's view of the same devices and users, so reports can show devices that never report and Google sign-in records.
