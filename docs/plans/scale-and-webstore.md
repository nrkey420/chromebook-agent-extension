# Scaling to 50,000 devices and moving to the Chrome Web Store

## 1. Sizing at 50k devices

The numbers below are **estimates to be replaced with pilot measurements** (test-plan scenario A3 and the pilot's
real events per device per day). Recalculate this table after two weeks of pilot data.

| Assumption | Value |
|---|---|
| Devices | 50,000 |
| Active on a school day | 70 % → 35,000 |
| Active hours | 6.5 h (23,400 s) |
| Navigations per active device per day | 500 (**measure this**) |
| Heartbeats per device per day | 78 (every 5 min) |
| Session/login/download events | ~10 |
| **Events per device per day** | **~590** |

| Derived load | Value |
|---|---|
| Events per school day | ~20.6 million |
| Average events/s during the day | ~880 |
| Peak events/s (3× average; bell + lunch) | ~2,600 |
| HTTP requests/s (each active device flushes about once a minute) | ~580 avg, several thousand in the first minutes after a bell or an outage |
| SQL `ActivityEvents` growth (~1.3 KB/row including its 5 indexes) | **~27 GB per school day** |
| Raw Blob files (one per request today) | ~15 million small blobs per day |
| Sentinel ingestion (~0.7 KB/event) | ~14 GB per day |

### What breaks in the current design

| Component | Today | Problem at 50k |
|---|---|---|
| Azure SQL | Serverless GP, 2 vCores, 32 GB, auto-pause | Full in about a day; 880–2,600 single-row round trips/s, each with `MERGE ... HOLDLOCK` and 5 index updates; auto-pause resume returns 503s fleet-wide. |
| SQL write path | One `SqlCommand` per event inside a transaction | Latency and lock contention grow with load; a slow SQL makes every request slow because the HTTP request waits on SQL. |
| Heartbeats | Stored as `ActivityEvents` rows and `IpObservations` rows | ~13 % of all rows, almost none of them useful after the IP is known. |
| Blob raw copy | One JSONL blob per request | Tens of millions of tiny blobs; costly to list, replay or query. |
| Sentinel | Every event to the Analytics tier, no retry | Cost scales with raw browsing volume; failures are not replayed. |
| Function App | Flex Consumption, max 40 instances | Probably fine for the fast path once SQL leaves the request path; raise the cap and load test. |
| Auth | One HMAC secret for all devices, in extension policy | One leaked secret lets anyone forge any device's events. |
| Rate limiter | In memory, per instance | Limits vary with instance count; fine as a safety net only. |
| Extension retry | Fixed 60 s retry, no jitter, no backoff | After an outage, 35k devices retry in lock-step (recovery storm). |
| Extension queue | Whole queue is one storage value, rewritten on every event | Slow on low-end devices once the queue is large (long offline periods). |

## 2. Target architecture

```text
Chromebook extension
   │  HTTPS, per-device auth, gzip, jittered retry with backoff
   ▼
Azure Front Door (custom domain, WAF, per-IP/device rate limits)
   ▼
Ingest Function (Flex Consumption)  — validate signature + schema, stamp public IP, publish, return 202
   ▼
Azure Event Hubs (Standard, auto-inflate; partition key = device id; 7-day retention = replay buffer)
   ├─► Event Hubs Capture ──► ADLS Gen2 (Parquet/Avro, partitioned by date)   = raw system of record
   ├─► SQL writer Function (Event Hubs trigger, batches of 500–1,000)
   │      SqlBulkCopy / table-valued parameter into a staging table, then set-based MERGE into
   │      Sessions, Devices, IpObservations, ActivityEvents (de-dup on EventId)
   └─► Sentinel writer Function ──► Logs Ingestion API (DCR), with retry and dead-letter
Google sync Function (timer) ──► SQL
Power BI (rollups + drill-through)    Sentinel workbook / analytics rules
```

Why: the HTTP request no longer waits for SQL or Sentinel, so a slow or paused database cannot cause fleet-wide
retries; Event Hubs absorbs the morning bell and recovery storms; each sink can fall behind and catch up
independently; replay is a consumer reset instead of a Blob scavenger hunt.

### Data tiering

| Data | Hot store | Retention (agree with legal) |
|---|---|---|
| Devices, users, sessions, logins, IP observations | Azure SQL | 1–3 years |
| Web activity (navigation, search, download) — detail | Azure SQL, partitioned by day, columnstore after 7 days | 30–90 days |
| Web activity — daily rollups | Azure SQL | 2+ years |
| All events for hunting | Log Analytics `ChromebookActivity_CL` | 90 days interactive; consider the Basic/Auxiliary (data lake) tier for `NAVIGATION` and keep `DOWNLOAD`/login events in Analytics — check current Sentinel pricing for 14 GB/day before choosing |
| Raw archive | ADLS Gen2 (Cool → Archive lifecycle) | Per records policy, e.g. 1 year |

Heartbeats: write to SQL only as an `IpObservations` row **when the IP changed** (or once an hour), plus updating
`Devices.ExtLastSeenUtc` and `Sessions.LastSeenUtc`; do not store them in `ActivityEvents`. Optionally exclude them
from Sentinel too.

### Azure SQL for production

- Provisioned **General Purpose or Hyperscale**, starting around 8 vCores; size from load test L2. No auto-pause.
- Hyperscale if SQL retention × daily growth goes beyond a few TB or you want fast scale-up/down.
- `ActivityEvents`: partition by `EventTimeUtc` (daily), clustered columnstore for closed partitions, keep only the
  indexes the procedures use; drop partitions for retention.
- Change `UX_ActivityEvents_EventId` de-dup to happen in the staging MERGE (a global unique index on a partitioned
  table must include the partition key).
- Zone redundancy, geo-backup, and a failover group if IR availability matters.

### Security changes

1. **Per-device authentication.** Replace the fleet-wide HMAC secret with ChromeOS device attestation
   (Verified Access: `chrome.enterprise.platformKeys.challengeKey` + Google's Verified Access API on the collector
   side) to issue a short-lived per-device token. Interim: rotate keys per OU/school (`HMAC_KEYS__<id>` already
   supports many keys) and block `chrome://policy` for student OUs.
2. **Custom domain** on Front Door (e.g. `chromebook-telemetry.district.org`) and narrow `host_permissions` to it
   instead of `https://*.azurewebsites.net/*`; lock the Function to accept traffic only from Front Door.
3. **Private endpoints** for SQL, Storage, Key Vault, Event Hubs; Function with VNet integration.
4. Defender for Cloud on the subscription; alerts on Key Vault secret reads.

### Extension changes before scale

| Change | Why |
|---|---|
| Random jitter on flush (0–60 s) and exponential backoff (cap 15 min) on 429/5xx, honouring `Retry-After` | Prevent recovery storms. |
| Collector returns per-event rejections; extension drops only the bad events | One bad event no longer loses 49 good ones. |
| Store the queue in chunks (e.g. one key per 500 events) or IndexedDB | Constant-cost enqueue on low-end devices. |
| `gzip` request bodies (`CompressionStream`) | ~5–8× less bandwidth on school Wi-Fi. |
| Send a final flush when the session ends / the screen locks (`chrome.idle`), and emit `LOCK`/`UNLOCK` | Fewer lost events on ephemeral carts; real session boundaries for IR. |
| Fix shared-device session end: on startup, end the previous session at its `lastSeenMs`, not "now" | Correct "who was on the device until when" (test S2). |
| Clock-skew handling (collector returns server time on 401; extension retries with corrected time) | Devices with wrong clocks stop looping on 401. |
| Configurable heartbeat interval via policy | Tune load per OU. |
| Remote kill switch / sampling via policy (e.g. `collectNavigation: false`) | Shed load or respond to a privacy request without a new release. |

### Operations

- Infra as code for everything above in Bicep, with `dev`, `test`, `prod` parameter files and the existing GitHub
  Actions deploy using environment approvals for prod.
- Dashboards and alerts from reporting plan section 2, page 6. Alert routing to the on-call channel.
- Runbooks: SQL behind on Event Hubs lag, Sentinel ingest failing, key rotation, Google sync failing, extension
  silent on a whole OU, replay from Event Hubs / ADLS.
- Capacity review each quarter against actual events/device/day.

## 3. Phased rollout with gates

| Phase | Devices | What changes | Gate to move on |
|---|---|---|---|
| 0 Lab | 5–10 | Current PoC + Google sync + `usp_WebActivity`. Run the full functional test plan (sections 3.1–3.6). | All scenarios recorded; blockers fixed. |
| 1 Pilot school | ~500 | Production subscription, provisioned SQL (no auto-pause), custom domain, jitter/backoff release. **Measure events/device/day.** | Reconciliation KPIs met for 10 school days; sizing table recalculated; mock investigations < 15 min. |
| 2 Early schools | ~5,000 | Event Hubs pipeline, bulk SQL writer, rollup tables, heartbeat change, Web Store item live. | Load tests L1–L4 pass at 50k in test; p95 delay < 2 min in prod; no 5xx spikes at bells. |
| 3 Half fleet | ~25,000 | Per-device auth, partitioned `ActivityEvents`, RLS/roles, Sentinel tiering decision. | L5 soak passes; cost per device per month within budget; IR sign-off. |
| 4 Full fleet | 50,000 | Remaining OUs in batches of ~5k per week. | Coverage ≥ 98 % per OU before adding the next batch. |

Rollback at every phase: remove the OU from the force-install list (Google Admin), which uninstalls the extension
from those devices; the backend keeps the data already collected.

## 4. Moving from self-hosted to the Chrome Web Store

### 4.1 Publisher setup

1. Create a **Google group** in your Workspace domain (e.g. `chrome-extension-publishers@district.org`) and register
   a Chrome Web Store developer account as a **group publisher** owned by it, so the listing does not depend on one
   person. Enable 2-step verification on every member. Pay the one-time registration fee.
2. Write and host a **privacy policy** page (district website) describing exactly what is collected (URLs, titles,
   searches, downloads, device and network identifiers, sign-in identity), why, who can see it, and retention. The
   Web Store requires it for the permissions this extension uses.

### 4.2 Keep the same extension ID (recommended)

The extension ID is derived from its signing key. If you upload the existing key with the **first** Web Store
upload, the store item keeps the ID you already use, so:
- the extension policy in Google Admin (keyed by ID) does not need re-entering;
- devices update in place — the event queue and session state in `chrome.storage.local` survive the switch, so
  nothing queued is lost;
- reports, filters and any allow-lists that reference the ID keep working.

How: copy your saved `.pem` into the root of the package as `key.pem`, zip, and upload that as the first version
(Chrome developer docs: "Upload a previously packaged extension"). Remove `key.pem` from all later uploads and never
commit it. **Verify on a throwaway item first** and confirm the ID in the dashboard matches before you change any
force-install settings. If the ID does not match, use the new-ID route in 4.5.

(`docs/self-hosted-extension.md` currently says the ID will change; update it once this is verified.)

### 4.3 Prepare the store package

| Item | Action |
|---|---|
| `update_url` | Strip it from the store build (`pack.sh` flag); the store manages updates. Keep it in the self-hosted build until cut-over is done. |
| `host_permissions` | Narrow to your collector custom domain (`COLLECTOR_HOST_PERMISSION` in `pack.sh`). A wildcard `*.azurewebsites.net` invites review questions. |
| Permissions justification | Fill in a reason for each: `history` (navigation events), `downloads`, `enterprise.deviceAttributes` / `enterprise.networkingAttributes` (device and IP attribution on managed ChromeOS), `identity` / `identity.email` (signed-in account), `alarms`, `storage`, `unlimitedStorage` (offline queue). |
| Single purpose statement | "Managed-device activity logging for the district's safeguarding and security incident response." |
| Data use disclosures | Declare web history, personally identifiable information (email), location-type data (IP); certify no sale or unrelated use. |
| Listing | Name, description, 128px icon, screenshots and promo tile (`extension/store-assets/`). |
| Visibility | **Private** — visible only to your Workspace domain (or to specific trusted testers). Not public or unlisted. |

Expect a longer review than a simple extension because of `history` and the enterprise permissions; allow 1–3
weeks for the first submission and reply promptly to reviewer questions.

### 4.4 Release channels

- **Two store items:** `Chromebook Agent (Beta)` force-installed only on IT/pilot OUs, and the production item for
  everyone else. Every release goes to Beta first for at least a week of school days. (The beta item has its own
  ID and its own policy entry.) Never install both on the same OU.
- **Versioning:** CI bumps/validates `manifest.json` version; the existing *Build extension package* workflow
  produces the zip. Add a manual-approval job that uploads to the store via the Chrome Web Store API (service
  account / OAuth client stored as a GitHub secret) — first to Beta, then promote to production.
- If your item qualifies, use the store's **percentage rollout** for production releases; otherwise roll out by
  moving the production item onto OUs in batches.
- Keep the self-hosted `.crx` + `update.xml` path as an emergency channel (e.g. if a review blocks an urgent fix)
  for the IT OU only.

### 4.5 Cut-over steps

1. Upload the first version (with `key.pem`), wait for approval, confirm the ID matches the self-hosted ID.
2. Pilot OU: in Google Admin → Apps & extensions → the extension → change **installation source** from the custom
   URL to the Chrome Web Store (same ID); keep the policy JSON unchanged.
3. Bump the version above the self-hosted one so devices take the store build; verify on lab devices
   (`chrome://extensions` shows "From Chrome Web Store"), then check `vw_Devices.ExtVersion` across the OU.
4. Move the remaining OUs. Stop publishing self-hosted releases except for the emergency channel.

**If the ID does change** (no key, or the key upload is not accepted): add the new ID to the pilot OU with the same
policy JSON, remove the old ID from that OU, and accept that events still queued in the old extension on those
devices are lost (do it outside school hours, after a flush). Because both run under different IDs, never leave
both force-installed on the same OU — every event would be recorded twice.

## 5. Work breakdown (suggested order)

1. ~~Reporting prerequisites~~ — Google sync, `usp_WebActivity` and `InvestigationAudit` are built.
2. Extension 0.4: jitter/backoff, per-event rejection, session-end fix, chunked queue, `LOCK`/`UNLOCK`, gzip.
3. Load simulator + test environment; run L1–L2 against the current design to get a baseline.
4. Event Hubs pipeline, bulk SQL writer, heartbeat change, Sentinel writer with retry.
5. Provisioned SQL + partitioning + rollups; Power BI on rollups.
6. Front Door + custom domain + private endpoints; narrow `host_permissions`.
7. Web Store publisher, privacy policy, Beta item, ID-preserving first upload, cut-over.
8. Per-device auth (Verified Access).
9. Phased rollout per section 3.
