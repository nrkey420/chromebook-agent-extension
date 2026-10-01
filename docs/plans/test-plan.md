# Test plan

Goal: prove that, for every condition a district Chromebook actually meets, each event is attributed to the right
**device**, **user** and **session**, arrives **once**, and is visible in **Blob, SQL and Sentinel** within the
expected delay — and that failures degrade safely (queue, retry, no silent loss).

## 1. Test environment

| Item | Setup |
|---|---|
| Azure | A separate `test` environment from the same Bicep (`environment=test`), its own SQL DB, DCR and Key Vault. Never test destructive cases against production. |
| Google Admin | A **test OU** with the extension force-installed and policy set (see `infra/scripts/set-extension-policy.md`). Turn `debug: true` on for lab devices only. |
| Devices | At least: 2 current-model Chromebooks, 1 low-end/older model (slow CPU, 4 GB RAM), 1 device on the oldest ChromeOS version you support. All enrolled, so the enterprise APIs work. |
| Accounts | 3 student test accounts and 1 staff test account in the test OU (affiliated users), 1 account from **outside** the domain, managed guest session enabled on one device. |
| Network | School LAN (behind your web filter / TLS inspection), guest Wi-Fi, a phone hotspot (off-campus public IP), and a way to cut the network (airplane mode / unplugging the AP). |
| Tools | `infra/scripts/send-test-batch.sh` (signed batches), SQL access (`sqlcmd` with Entra), Log Analytics query access, a load generator (section 6). |

**Record for every test:** device serial, account, start/end time (local and UTC), extension version, ChromeOS
version, network, and the expected vs. actual rows.

## 2. Standard checks (run after every scenario)

Use these queries; each scenario lists what the result must be. Replace the serial and time window.

```sql
-- A. Sessions on the device in the window
DECLARE @Serial nvarchar(128) = '5CD1234XYZ', @FromUtc datetime2 = '2026-10-01T13:00', @ToUtc datetime2 = '2026-10-01T15:00';
SELECT s.SessionId, s.UserEmail, s.SessionStartUtc, s.LoginUtc, s.LogoutUtc, s.SessionEndUtc, s.EndReason, s.IsActive,
       s.FirstInternalIp, s.LastPublicIp, s.ExtensionVersion
FROM dbo.Sessions s JOIN dbo.Devices d ON d.DirectoryDeviceId = s.DirectoryDeviceId
WHERE d.SerialNumber = @Serial AND s.SessionStartUtc BETWEEN @FromUtc AND @ToUtc
ORDER BY s.SessionStartUtc;

-- B. Event counts by type, and lateness (received minus event time)
SELECT a.EventType, COUNT(*) AS Events, COUNT(DISTINCT a.SessionId) AS Sessions,
       MAX(DATEDIFF(second, a.EventTimeUtc, a.ReceivedUtc)) AS MaxDelaySec,
       AVG(DATEDIFF(second, a.EventTimeUtc, a.ReceivedUtc)) AS AvgDelaySec
FROM dbo.ActivityEvents a JOIN dbo.Devices d ON d.DirectoryDeviceId = a.DirectoryDeviceId
WHERE d.SerialNumber = @Serial AND a.EventTimeUtc BETWEEN @FromUtc AND @ToUtc
GROUP BY a.EventType;

-- C. Events with no user or no device (attribution failures)
SELECT EventType, COUNT(*) FROM dbo.ActivityEvents
WHERE EventTimeUtc BETWEEN @FromUtc AND @ToUtc AND (UserEmail IS NULL OR DirectoryDeviceId = 'unknown')
GROUP BY EventType;

-- D. Collector errors in the window
SELECT * FROM dbo.IngestionErrors WHERE ErrorTimeUtc BETWEEN @FromUtc AND @ToUtc ORDER BY ErrorTimeUtc;
```

```kql
// E. Same events in Sentinel (compare the count with query B)
ChromebookActivity_CL
| where TimeGenerated between (datetime(2026-10-01T13:00:00Z) .. datetime(2026-10-01T15:00:00Z))
| where DeviceSerial == "5CD1234XYZ"
| summarize Events = count(), Sessions = dcount(SessionId) by EventType
```

**Global pass rules** (apply to every scenario unless it says otherwise):
- SQL count (B) = Sentinel count (E) = number of events in the raw blobs for that device and window.
- No duplicate `EventId` (enforced by `UX_ActivityEvents_EventId`; check Sentinel with `summarize count() by EventId | where count_ > 1`).
- Query C returns nothing for affiliated users on enrolled devices.
- Online delay (B, `MaxDelaySec`) ≤ `flushIntervalMs` + 60 s.

## 3. Functional scenarios

### 3.1 Session and login attribution

| # | Scenario | Steps | Expected |
|---|---|---|---|
| S1 | First sign-in after install | Fresh device, sign in, browse 5 sites. | One session; `SESSION_START` + `LOGIN` + `NAVIGATION`×5 + `HEARTBEAT`s; `LoginUtc` set; `IsActive = 1`. |
| S2 | Sign out / next user (shared cart device) | User A signs in, browses, signs out; user B signs in, browses. | Two sessions, each with the right `UserEmail`; no A events attributed to B. **Expected gap to confirm:** on ChromeOS each user has a separate profile with its own copy of the extension and its own `chrome.storage.local`, so B's extension never sees A's session. A's `SESSION_END` is only written the *next time A signs in on that device* (timestamped then, possibly days later) or never, and `LOGOUT` is never emitted (it only fires on a user change inside one profile). Until fixed, use `Sessions.LastSeenUtc` (last heartbeat, ≤ 5 min accuracy) or the Google `CHROME_OS_LOGOUT_EVENT` as the real end time. |
| S2b | Shared device in ephemeral mode | Same as S2 with `DeviceEphemeralUsersEnabled` on (common on carts). | The profile is wiped at sign-out, so **any events still queued at sign-out are lost** (up to one flush interval, more if offline). Measure how many; consider a shorter `flushIntervalMs` for cart OUs. |
| S3 | Same user signs back in | A signs out and back in. | New session; the previous one gets `SESSION_END` with `EndReason = restart`, timestamped at the new sign-in (see S2). |
| S4 | Inactivity timeout | Sign in, leave idle 25 min (default timeout 20), then browse. | Old session `EndReason = inactivity`; new session starts at the next event. Heartbeats keep firing while idle — **check whether heartbeats reset the timeout** (they do today: every event updates `lastSeenMs`, so a session never times out while the device is awake). Decide whether that is the intended behaviour and document it. |
| S5 | Lock screen / sleep | Close the lid 10 min, reopen, unlock. | Same session continues; heartbeats stop during sleep and resume; no data loss. (The extension does not emit `LOCK`/`UNLOCK` today, although the collector treats `UNLOCK` as an IP observation — note as a gap if IR needs it.) |
| S6 | Reboot / power loss | Browse, hold power to force off, boot, sign in. | Queued but unsent events are sent after boot (they are in `chrome.storage.local`); previous session closed with `EndReason = restart`. |
| S7 | ChromeOS update restart | Apply an OS update during a session. | Same as S6. |
| S8 | Multiple profiles (multi sign-in) | Add a second account to the session (if allowed by policy). | Each profile runs its own copy of the extension: confirm which profile's email is reported and whether two parallel sessions appear on one device. Decide the policy (most districts disable multi sign-in). |
| S9 | Non-affiliated / outside account | Sign in with the outside account (if permitted). | Enterprise device APIs return nothing → `DirectoryDeviceId = 'unknown'`. Confirm volume and decide whether to block such sign-ins by policy. |
| S10 | Managed guest session | Start a managed guest session and browse. | Confirm whether the extension runs and what `UserEmail` is (expected null). Attribution relies on device + time. |
| S11 | Compare with Google audit | After S1–S7, once the Google sync exists. | Each extension `LOGIN` has a matching `CHROME_OS_LOGIN_EVENT` within ±2 min for the same device and user (`vw_LoginHistory`). |

### 3.2 Activity capture

| # | Scenario | Expected |
|---|---|---|
| A1 | Normal browsing (typed URL, link click, back/forward, new tab) | One `NAVIGATION` per history visit with URL, `Domain`, title (if `collectTitles`). |
| A2 | Searches on Google, Bing, YouTube, DuckDuckGo, Yahoo, Ecosia, Brave | `SearchEngine` and `SearchQuery` populated (derived server-side by `EventEnricher`). Include a non-ASCII query and one with `+`/`%20`. |
| A3 | Single-page apps (YouTube, Google Docs, Classroom) | Record how many `NAVIGATION`s one minute of use produces — this drives sizing. |
| A4 | Download: safe file, blocked/dangerous file, cancelled download | `DOWNLOAD` rows with file name, MIME, danger, state transitions. Check that the follow-up `onChanged` events (which carry no URL) are still useful or should be merged. |
| A5 | Incognito / guest browsing | Normally disabled by policy; confirm it is, or confirm extension behaviour if not. |
| A6 | `collectTitles: false` | Title null in Blob, SQL and Sentinel. |
| A7 | Very long URL (> 2,048 chars), long title, emoji, RTL text | Stored clamped (2,048 / 1,024), batch still accepted. |
| A8 | `chrome://`, `file://`, `data:` and extension pages | Decide whether these should be filtered client-side; confirm no failure. |

### 3.3 Network conditions

| # | Scenario | Expected |
|---|---|---|
| N1 | School LAN behind web filter / TLS inspection | Requests to the collector are allowed (bypass/allow-list the collector host). `PublicIp` is the school's egress IP, not the filter vendor's cloud IP — **if the filter proxies traffic through its cloud, PublicIp attribution is wrong**; check and document. |
| N2 | Off-campus (hotspot / home) | `PublicIp` = home/carrier IP; `InternalIp` = private LAN IP. |
| N3 | Offline 10 min, 8 h, 3 days | All events delivered after reconnect, original `EventTimeUtc` kept, no duplicates. 3 days of heavy use may exceed `MAX_QUEUE` (20,000): then the next heartbeat must carry `queue_full_dropped=<n>` in `Detail`. Measure battery/CPU impact while the queue is large (the whole queue is one storage value rewritten on every event). |
| N4 | Flapping Wi-Fi (on/off every 30 s for 10 min) | No loss, no duplicates. |
| N5 | Captive portal (guest Wi-Fi before accepting terms) | Sends fail and are retried; nothing dropped. |
| N6 | IPv6-only or dual-stack network | `InternalIpv6` populated; public IP resolves correctly. |
| N7 | Network change mid-session (LAN → hotspot) | IP changes appear in `IpObservations` within one heartbeat (≤ 5 min) and in `vw_IpHistory`. |

### 3.4 Device conditions

| # | Scenario | Expected |
|---|---|---|
| D1 | Device clock 10 min fast / slow | HMAC allows ±300 s, so **every batch gets 401 and is retried forever**. Confirm, and decide: widen skew, have the collector return its time so the extension corrects, or rely on ChromeOS time sync. |
| D2 | Low disk space | Queue writes may fail; confirm no crash loop and events resume when space returns. |
| D3 | Extension update with events queued | Push a new version while offline with a queue; on reconnect old events send under the new version. |
| D4 | Policy missing / wrong secret / wrong URL | Missing: events queue locally, nothing sent. Wrong secret: 401s, queue grows, recovers after fix. |
| D5 | Policy change while running | Changing `flushIntervalMs` re-schedules the alarm without reinstall. |
| D6 | Old ChromeOS version / slow device | Same results as a current device; note any extension errors in `chrome://extensions` (debug on). |

### 3.5 Collector and backend failure modes

| # | Scenario | How | Expected |
|---|---|---|---|
| B1 | SQL paused (serverless auto-pause) | Let the DB pause, then send. | First batch may get 503 while SQL resumes (~1 min); retried; all rows land. Production should not auto-pause (see scale plan). |
| B2 | SQL unavailable | Block the Function's SQL access (firewall or remove the DB user) for 15 min. | Blob still written, 503 returned, extension retries, `IngestionErrors` rows logged, full recovery after restore. |
| B3 | Sentinel unavailable | Point `DCR_IMMUTABLE_ID` at a wrong value. | Batch accepted (202), SQL complete, Sentinel missing those events, `IngestionErrors` layer `Sentinel`. **Known gap:** no automatic Sentinel replay — test the manual replay from Blob. |
| B4 | Blob unavailable | Remove the Function's Storage role. | 5xx, extension retries, no partial SQL writes. |
| B5 | Key rotation | Add `HMAC_KEYS__KEY2`, roll policy to KEY2 for the test OU, then remove KEY1. | No 401 window during the overlap. |
| B6 | Collector redeploy during traffic | Run the deploy workflow while devices send. | Only transient retries, no loss. |
| B7 | Rate limit | Send > 30 requests in a burst from one device id. | 429, then recovery. |

### 3.6 Security

| # | Test | Expected |
|---|---|---|
| X1 | Replay a captured request after 6 min | 401 (timestamp skew). **Within** 5 min a replay is accepted, but de-duplicated by `EventId` — confirm no duplicate rows. |
| X2 | Tamper one byte of the body | 401. |
| X3 | Unknown `X-Key-Id` | 401. |
| X4 | Body > `MAX_BODY_BYTES`, malformed JSON, empty events, wrong types | 413 / 400, nothing written. |
| X5 | SQL/HTML/KQL injection strings in URL, title, filename | Stored as text; Power BI and Sentinel show them inert. |
| X6 | Forge events for another device using the shared secret | **Currently succeeds** — this is the threat the per-device auth in the scale plan removes. Record it as an accepted PoC risk. |
| X7 | Read the secret as a student | Check whether `chrome://policy` (and `chrome://extensions` details) shows `sharedSecret` to the signed-in student. If so, block those pages for student OUs (`URLBlocklist`) as an interim control. |

## 4. Data-accuracy reconciliation (daily during pilot)

Run these as scheduled queries and track them as pilot KPIs:

| KPI | Target |
|---|---|
| Coverage: enrolled devices active in Google in the last 24 h that also reported via the extension | ≥ 98 % (needs Google sync) |
| Extension `LOGIN` events with a matching Google `CHROME_OS_LOGIN_EVENT` (±2 min) | ≥ 99 % |
| Events attributed (non-null `UserEmail` and known device) | ≥ 99.5 % for affiliated users |
| SQL vs Sentinel vs Blob event counts per day | Equal (difference = `IngestionErrors` count) |
| p95 delivery delay while online | ≤ 2 min |
| Heartbeats with `queue_full_dropped` | 0 under normal use |

## 5. Automated tests to add

- **Extension (node `--test`, existing `fake-chrome.mjs`):** user switch mid-queue (S2), inactivity with heartbeats (S4), 400 with one bad event (should not lose the other 49 once per-event rejection exists), queue overflow counter (N3), clock-skew 401 loop (D1), retry backoff/jitter once added.
- **Collector (xUnit):** batch with one invalid event, duplicate `EventId` across two batches, Sentinel failure path writes `IngestionErrors`, rate limiter keyed per device.
- **SQL integration test in CI:** spin up SQL Server in a container (`mcr.microsoft.com/mssql/server`), run `001`–`003`, insert fixtures, and assert the view and procedure outputs (`usp_DeviceTimeline`, `usp_WhoWasOnIp`, `vw_LoginHistory`). Today `SqlSyntaxTests` only parses the scripts.
- **End-to-end smoke after each deploy:** `send-test-batch.sh` with a fixed `EventId`, then assert it in SQL and Sentinel; fail the workflow if missing after 5 min.

## 6. Load and soak tests

**Status: simulator built** (`tools/load-simulator/`, with commands for L1–L6 in its README). Not yet run against an Azure test environment.

Real devices cannot produce 50,000-device traffic, so build a **device simulator**: a small Node or k6 script that
creates N virtual devices (distinct `X-Device-Id`, serial, user), each following a realistic schedule — sign-in
burst at the bell, heartbeat every 5 min, navigation at the rate measured in pilot (scenario A3), flush every
60 s — signing each batch exactly like `extension/src/crypto.js`. Run it from Azure (Container Apps jobs or Azure
Load Testing) so the client side is not the bottleneck. Point it at the **test** environment only.

| Test | Profile | Pass |
|---|---|---|
| L1 Baseline | 1,000 devices, 30 min | p95 latency < 500 ms, 0 % 5xx, SQL CPU < 50 % |
| L2 Step-up | 5k → 10k → 25k → 50k → 75k devices, 15 min each | Find the first component to saturate; all pass criteria at 50k |
| L3 Morning bell | 35k devices sign in within 10 min | No 5xx; all `SESSION_START`/`LOGIN` in SQL within 5 min |
| L4 Recovery storm | Collector offline 30 min, 35k devices then reconnect with full queues | Backlog cleared within 30 min, no 5xx cascade, no lost events |
| L5 Soak | 50k devices for a full simulated school day (6.5 h) + 24 h | No memory growth in the Function, no SQL log growth problems, reports still fast |
| L6 Reporting under load | Run the dashboard queries and `usp_DeviceTimeline` during L2 at 50k | Investigation queries < 10 s, ingestion unaffected |

Measure: requests/s, events/s, p50/p95/p99 latency, 4xx/5xx by code, Function instance count, SQL CPU / log IO /
waits / deadlocks, Blob request rate, Sentinel ingestion latency, and end-to-end delay (event time → row in SQL).

## 7. Exit criteria to leave the pilot

- Every scenario in section 3 has a recorded result; all failures are either fixed or documented as accepted.
- Reconciliation KPIs (section 4) met for 10 consecutive school days.
- L1–L6 pass against the production-shaped architecture from the scale plan.
- IR team has run three mock investigations (section 5 of the reporting plan) end to end.
