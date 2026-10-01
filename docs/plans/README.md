# Plans: test, report, scale

The PoC works end to end for a handful of devices. These plans take it to a 50,000-device production service
with incident-response reporting and a Chrome Web Store–hosted extension.

| Plan | What it covers |
|---|---|
| [test-plan.md](test-plan.md) | How to prove the extension + collector behave as expected under real-world conditions (shared devices, offline, clock skew, outages, load). Test matrix with pass criteria and the SQL to check each one. |
| [reporting-and-dashboards.md](reporting-and-dashboards.md) | Triage and incident-response reporting: device counts, logins, logins to a specific device, pulling web activity. Power BI pages, Sentinel workbook/KQL, SQL procedures to add, access control and evidence handling. |
| [scale-and-webstore.md](scale-and-webstore.md) | Sizing for 50k devices, the architecture changes needed to get there, a phased rollout with go/no-go gates, and the move from the self-hosted `.crx` to the Chrome Web Store. |

## Gaps found while writing these plans

These are in the current code/docs and are called out where they matter in each plan:

1. ~~**The Google sync job does not exist.**~~ **Built** — see `docs/google-sync.md`. It fills `GoogleAuditEvents`,
   `GoogleUsers`, `GoogleDeviceRecentUsers`, `GoogleDeviceActiveTime`, `SyncState` and the Google columns of `Devices`.
   It needs the one-time Google service account / delegation setup before it does anything.
2. **SQL is sized for the PoC.** 2 vCores / 32 GB, one SQL round trip (with `MERGE ... HOLDLOCK`) per event, and
   every event — including 5-minute heartbeats — stored in `ActivityEvents`. See the sizing in
   [scale-and-webstore.md](scale-and-webstore.md#1-sizing-at-50k-devices).
3. **One HMAC secret for the whole fleet, delivered through extension policy.** Anyone who can read the policy
   on a device (for example on `chrome://policy`) can sign events as any device.
4. ~~**`docs/powerbi-reporting.md` lists views that do not exist**~~ — fixed; it now points to `reporting/powerbi/`.
   Was: (`vw_CurrentActiveSessions`,
   `vw_DeviceLoginHistory`, …). The real views are in `002_views.sql`; the reporting plan uses those.
5. **No retry for Sentinel.** If Logs Ingestion fails, the batch is still accepted (SQL succeeded), so those
   events never reach Sentinel unless replayed from Blob.
6. **One bad event drops the whole batch.** The collector returns 400 for any schema error and the extension
   then discards all (up to 50) events in that batch.
7. **Session end times on shared devices are wrong.** Each ChromeOS user has their own extension storage, so a
   session is only closed when the *same* user next signs in on that device, and `LOGOUT` is never sent. Logins
   are right; "who was on the device until when" needs `LastSeenUtc` or Google logout events (test S2).
8. **`docs/self-hosted-extension.md` says the Web Store ID will differ.** It does not have to: uploading the
   existing key keeps the ID (see the Web Store section).
