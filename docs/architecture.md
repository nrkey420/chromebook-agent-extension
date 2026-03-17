# Architecture
```text
Chrome Extension -> Function Collector -> Blob(raw) + SQL(normalized) + Sentinel(hunting)
                                         -> Power BI (DirectQuery on SQL views)
```
Blob keeps immutable raw events, SQL stores attribution/reporting records, Sentinel stores hunting-friendly normalized security records.
