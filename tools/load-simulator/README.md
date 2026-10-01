# Load simulator

Simulates thousands of Chromebooks running the extension against a collector, to answer: **how does the current
design behave at 1k, 5k, 25k, 50k devices, after the morning bell, and after an outage?** (test plan
`docs/plans/test-plan.md`, section 6).

Each virtual device behaves like extension v0.3:
- At sign-in it sends SESSION_START, LOGIN and a heartbeat, and flushes straight away.
- It sends heartbeats every 5 minutes, browses at a configurable rate (with occasional downloads), and flushes its
  queue every 60 s (up to 20 batches × 50 events).
- It retries on 5xx or network errors, drops a batch on 400/413, and caps its queue at 20,000 events.
- Requests carry the same body, headers and HMAC signature as the extension. The signing code is the extension's own
  `extension/src/crypto.js`.

**Test environments only.** Every simulated event is really written to Blob, Azure SQL and (if configured) Sentinel in
the target environment. All simulated data is tagged so it can be removed afterwards (see [Cleaning up](#cleaning-up)):
device IDs `loadtest-NNNNNN`, serials `LOADTESTNNNNNN`, users `loadtest-NNNNNN@loadtest.invalid`, extension version
`0.0.0-loadtest`.

Requires Node.js 20+ and no packages.

## Try it without Azure

```bash
cd tools/load-simulator
node simulator.mjs --target mock --devices 500 --duration 60 --ramp-sec 10
```

`--target mock` starts a local stand-in collector that checks signatures and payloads the way the real one does. Add
`--mock-failure-rate 0.1` to see retries at work. Every event should still arrive exactly once.

## Run against a test environment

```bash
# The HMAC secret for the key the test environment accepts (KEY1 by default)
export LOADTEST_SHARED_SECRET=$(az keyvault secret show --vault-name <test-key-vault> -n HmacKey1 --query value -o tsv)

node simulator.mjs --target https://<test-function-host> --confirm-host <test-function-host> \
  --profile baseline --devices 1000 --duration 1800 --out l1-baseline.json
```

`--confirm-host` must repeat the target's host name; the simulator refuses to start without it.

### The test plan's load tests

| Test | Command (add `--target … --confirm-host …`) | Pass |
|---|---|---|
| L1 Baseline | `--profile baseline --devices 1000 --duration 1800` | p95 < 500 ms (`--max-p95-ms 500`), 0 % errors (`--max-error-rate 0`) |
| L2 Step-up | `--profile step --steps 5000,10000,25000,50000,75000 --step-sec 900` | Find the first level where p95 or errors break; all checks pass at 50k |
| L3 Morning bell | `--profile bell --devices 35000 --bell-sec 600 --duration 1800` | No 5xx; all events delivered |
| L4 Recovery storm | `--profile recovery --devices 35000 --outage-sec 1800 --duration 3000` | Backlog cleared, no 5xx cascade, all events delivered |
| L5 Soak | `--profile soak --devices 50000 --duration 23400` (6.5 h) | Stable latency for the whole run |
| L6 Reporting under load | Run L2 at 50k and, meanwhile, the Power BI refresh and `usp_DeviceTimeline` | Reports < 10 s; ingestion unaffected |

Before L2–L5, replace the defaults with your pilot's real numbers: `--nav-per-hour` (navigations per device per hour;
default 77 ≈ 500 per 6.5 h school day) and the share of devices active. Run each test twice: as-is and with `--jitter`,
which spreads each device's sends the way the planned extension fix would. The difference shows what the fix buys.

### Where to run it

The test client must not be the bottleneck. Measured against the local mock collector: **one 4-core machine
simulated 50,000 devices at realistic rates (about 850 requests/s on average, 1,350/s at peak) using about a third
of its CPU.**

- Up to ~5,000 devices: Azure Cloud Shell or a laptop is enough.
- 50,000 to 75,000 devices: a temporary 4–8 core Azure VM in the **same region** as the collector (for example
  `Standard_D8s_v5`), with `--processes 8`. Delete it afterwards.
- More: split across machines with `--device-offset`, so each machine simulates its own devices. For example, machine
  A runs `--devices 40000`, and machine B runs `--devices 35000 --device-offset 40000`.

If the summary prints **"the simulator fell behind its own schedule"**, the client was overloaded and the results
understate the load. Add processes or machines.

## Reading the results

A progress line every `--report-sec` (default 10 s):

```
   120s  devices  35000  req/s   612.4  events/s   1104  p95    84ms  errors 0  backlog 2210
```

At the end, a JSON summary (also written to `--out`):

| Field | Meaning |
|---|---|
| `requestsPerSec`, `eventsPerSec` | Average throughput the collector accepted |
| `status` | Responses by code (`2xx`, `429`, `503`, `network`, `timeout`) |
| `latencyMs` | Request latency p50/p95/p99/max |
| `deliveryDelaySec` | Event age when the collector accepted it (event time → accepted). The test plan wants p95 < 2 min |
| `backlogLeft`, `droppedQueueFull` | Events never delivered. Both should be 0 |
| `clientLagMsP95` | How late the simulator started sends; high values mean the client was saturated |
| `checks`, `passed` | p95 and error rate against `--max-p95-ms` / `--max-error-rate`, and all events delivered. Exit code 1 if not passed |

Meanwhile, watch the server side:
- Application Insights **Live Metrics** for the Function App: requests, failures, instance count.
- Azure SQL **Metrics**: CPU, Data IO and Log IO percentages, and deadlocks.
- `dbo.IngestionErrors` for SQL or Sentinel write failures.

The sizing in `docs/plans/scale-and-webstore.md` predicts SQL as the first bottleneck; this is how to confirm it.

## Costs and side effects

- **Azure SQL** (serverless) scales up during the test and bills for it. A 50k-device hour writes about 6 GB of rows,
  and the PoC database has a 32 GB size limit. Clean up after each run.
- **Blob storage**: one small file per request.
- **Sentinel / Log Analytics** bills per GB ingested (roughly 0.7 KB per event). Prefer a test environment without the
  DCR settings, or budget for it.
- The collector's per-device rate limit (30 requests burst, 1/s refill) is far above what one simulated device sends.

## Cleaning up

```bash
# SQL: deletes every row whose device ID starts with "loadtest-" (in chunks; safe to re-run)
sqlcmd -S <sqlServerFqdn> -d <sqlDatabaseName> --authentication-method ActiveDirectoryDefault -b \
  -i infra/scripts/sql/delete-load-test-data.sql

# Raw archive: blobs are stored under <keyId>/<deviceId>/...
az storage blob delete-batch --account-name <storageAccountName> --source chrome-activity-raw \
  --pattern 'KEY1/loadtest-*' --auth-mode login
```

Sentinel rows cannot be deleted selectively; exclude them in queries with
`| where DirectoryDeviceId !startswith "loadtest-"`, or let the table's retention expire them.

## Developing the simulator

```bash
node --test tests/*.test.mjs
```

The tests cover device behaviour, profiles, metrics, the CLI's safety checks, and an end-to-end run against the mock
collector with injected failures. They run in CI.

It has also been run against the real collector locally (`func start` with Azurite and SQL Server in Docker). All
simulated events were accepted and written to SQL exactly once.
