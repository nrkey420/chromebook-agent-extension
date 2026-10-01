#!/usr/bin/env node
// Chromebook load simulator: many virtual Chromebooks running the extension against a collector.
// TEST ENVIRONMENTS ONLY. Usage and profiles: tools/load-simulator/README.md
import { fork } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import os from 'node:os';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runShard } from './lib/engine.mjs';
import { createTransport } from './lib/transport.mjs';
import { startMockCollector } from './lib/mock-collector.mjs';
import { PROFILES } from './lib/profiles.mjs';
import { errorCount, mergeWindow, newWindow, percentile } from './lib/metrics.mjs';

const HELP = `Chromebook load simulator (test environments only)

  node simulator.mjs --target <collectorUrl|mock> --profile <name> [options]

Target
  --target URL|mock        Collector base URL (e.g. https://app-test.azurewebsites.net), or "mock" for a local dry run
  --confirm-host HOST      Required for a real target: must equal the URL's host name (guards against hitting the wrong environment)
  --key-id ID              HMAC key id (default KEY1). Secret: env LOADTEST_SHARED_SECRET (base64)

Profile (docs/plans/test-plan.md, section 6)
  --profile NAME           baseline | step | bell | recovery | soak (default baseline)
  --devices N              Devices (default 1000)
  --duration SEC           Test length (default 900; step: steps x step-sec)
  --ramp-sec SEC           Sign-in spread for baseline/soak/recovery (default 300)
  --steps LIST             step: device levels (default 1000,5000,10000,25000,50000,75000)
  --step-sec SEC           step: seconds per level (default 900)
  --bell-sec SEC           bell: everyone signs in within this many seconds (default 600)
  --outage-sec SEC         recovery: how long devices cannot send (default 1800)

Device behaviour (defaults = extension v0.3 + sizing estimate; replace with pilot measurements)
  --nav-per-hour N         Navigations per active device per hour (default 77, ~500 per 6.5 h school day)
  --download-share F       Share of navigations that are downloads (default 0.005)
  --flush-sec SEC          Send interval (default 60)    --heartbeat-sec SEC  Heartbeat interval (default 300)
  --batch-size N           Events per request (default 50) --max-batches N    Batches per flush (default 20)
  --jitter                 Spread each device's first flush over one interval (the proposed fix; real v0.3 has none)

Runner
  --processes N            Worker processes (default: CPU count, max 8). Use several machines for >~20k req/min
  --max-in-flight N        Concurrent requests per process (default 256)
  --timeout-sec SEC        Request timeout (default 30)
  --drain-sec SEC          After the test, keep sending queued events for up to this long (default 120)
  --report-sec SEC         Progress line interval (default 10)
  --seed N                 Random seed (default 1)
  --device-offset N        First device number (default 0). Give each machine its own range when splitting a test
  --out FILE               Write the summary as JSON

Pass/fail (exit code 1 on failure)
  --max-p95-ms MS          Request latency p95 (default 2000)
  --max-error-rate F       Non-2xx share of requests (default 0.01)

Dry-run mock options: --mock-latency-ms MS (default 5), --mock-failure-rate F (default 0)
`;

function parse(argv) {
  const { values } = parseArgs({ args: argv, allowPositionals: false, options: {
    help: { type: 'boolean' }, worker: { type: 'string' },
    target: { type: 'string' }, 'confirm-host': { type: 'string' }, 'key-id': { type: 'string', default: 'KEY1' },
    profile: { type: 'string', default: 'baseline' }, devices: { type: 'string', default: '1000' },
    duration: { type: 'string' }, 'ramp-sec': { type: 'string', default: '300' },
    steps: { type: 'string', default: '1000,5000,10000,25000,50000,75000' }, 'step-sec': { type: 'string', default: '900' },
    'bell-sec': { type: 'string', default: '600' }, 'outage-sec': { type: 'string', default: '1800' },
    'nav-per-hour': { type: 'string', default: '77' }, 'download-share': { type: 'string', default: '0.005' },
    'flush-sec': { type: 'string', default: '60' }, 'heartbeat-sec': { type: 'string', default: '300' },
    'batch-size': { type: 'string', default: '50' }, 'max-batches': { type: 'string', default: '20' }, jitter: { type: 'boolean', default: false },
    processes: { type: 'string' }, 'max-in-flight': { type: 'string', default: '256' }, 'timeout-sec': { type: 'string', default: '30' },
    'drain-sec': { type: 'string', default: '120' }, 'report-sec': { type: 'string', default: '10' }, 'tick-ms': { type: 'string', default: '250' },
    seed: { type: 'string', default: '1' }, 'device-offset': { type: 'string', default: '0' }, out: { type: 'string' },
    'max-p95-ms': { type: 'string', default: '2000' }, 'max-error-rate': { type: 'string', default: '0.01' },
    'mock-latency-ms': { type: 'string', default: '5' }, 'mock-failure-rate': { type: 'string', default: '0' },
  } });
  return values;
}

export function buildOptions(v) {
  const num = (k) => { const n = Number(v[k]); if (!Number.isFinite(n) || n < 0) throw new Error(`--${k} must be a non-negative number`); return n; };
  if (!PROFILES[v.profile]) throw new Error(`Unknown --profile ${v.profile}. Use one of: ${Object.keys(PROFILES).join(', ')}`);
  const o = {
    profile: v.profile, devices: Math.floor(num('devices')), rampSec: num('ramp-sec'),
    steps: v.steps.split(',').map((s) => Math.floor(Number(s.trim()))).filter((n) => n > 0),
    stepSec: num('step-sec'), bellSec: num('bell-sec'), outageSec: num('outage-sec'),
    navPerHour: num('nav-per-hour'), downloadShare: num('download-share'),
    flushMs: num('flush-sec') * 1000, heartbeatMs: num('heartbeat-sec') * 1000,
    batchSize: Math.max(1, Math.floor(num('batch-size'))), maxBatchesPerFlush: Math.max(1, Math.floor(num('max-batches'))), maxQueue: 20_000,
    jitter: v.jitter, maxInFlight: Math.max(1, Math.floor(num('max-in-flight'))), timeoutMs: num('timeout-sec') * 1000,
    drainSec: num('drain-sec'), reportMs: num('report-sec') * 1000, tickMs: Math.max(10, num('tick-ms')),
    seed: Math.floor(num('seed')), deviceOffset: Math.floor(num('device-offset')), maxP95Ms: num('max-p95-ms'), maxErrorRate: num('max-error-rate'),
    extensionVersion: '0.0.0-loadtest',
  };
  const p = PROFILES[o.profile];
  o.durationSec = v.duration !== undefined ? num('duration') : (p.duration ? p.duration(o) : 900);
  o.maxDevices = p.maxDevices ? p.maxDevices(o) : o.devices;
  // Recovery: ramp, 60 s steady, the outage, then at least two send intervals (min 30 s) to watch the reconnect.
  const afterOutage = Math.max(30, (2 * o.flushMs) / 1000);
  if (o.profile === 'recovery' && o.durationSec < o.rampSec + 60 + o.outageSec + afterOutage)
    throw new Error(`recovery needs --duration of at least ramp + 60 + outage + ${afterOutage} = ${o.rampSec + 60 + o.outageSec + afterOutage}s to see the reconnect`);
  return o;
}

function summarize(total, o, wallSec, extra = {}) {
  const l = total.latency, d = total.delivery, lag = total.schedulerLag;
  const errors = errorCount(total);
  const errorRate = total.requests ? errors / total.requests : 0;
  const s = {
    profile: o.profile, plan: PROFILES[o.profile].describe(o), wallSeconds: Math.round(wallSec),
    requests: total.requests, offlineSendAttempts: total.offlineAttempts, requestsPerSec: +(total.requests / wallSec).toFixed(1),
    eventsGenerated: total.generatedEvents, eventsAccepted: total.acceptedEvents, eventsPerSec: +(total.acceptedEvents / wallSec).toFixed(1),
    status: total.status, errorRate: +errorRate.toFixed(4),
    latencyMs: { p50: percentile(l, 50), p95: percentile(l, 95), p99: percentile(l, 99), max: Math.round(l.max) },
    deliveryDelaySec: { p50: secs(percentile(d, 50)), p95: secs(percentile(d, 95)), max: secs(d.max) },
    clientLagMsP95: percentile(lag, 95),
    droppedRejected: total.droppedRejected, droppedQueueFull: total.droppedQueueFull,
    ...extra,
  };
  s.checks = {
    latencyP95: s.latencyMs.p95 === null || s.latencyMs.p95 <= o.maxP95Ms,
    errorRate: errorRate <= o.maxErrorRate,
    allEventsDelivered: (extra.backlogLeft ?? 0) === 0 && total.droppedQueueFull === 0,
    simulatorKeptUp: (s.clientLagMsP95 ?? 0) <= Math.max(5000, o.flushMs),
  };
  s.passed = s.checks.latencyP95 && s.checks.errorRate && s.checks.allEventsDelivered;
  return s;
}
const secs = (ms) => (ms === null ? null : +(ms / 1000).toFixed(1));

function line(t, win, active, backlog, offline) {
  const sec = Math.max(1, win.spanMs / 1000);
  const errs = errorCount(win);
  return `${String(Math.round(t / 1000)).padStart(6)}s  devices ${String(active).padStart(6)}  req/s ${(win.requests / sec).toFixed(1).padStart(7)}  ` +
    `events/s ${(win.acceptedEvents / sec).toFixed(0).padStart(6)}  p95 ${String(percentile(win.latency, 95) ?? '-').padStart(5)}ms  ` +
    `errors ${errs}${errs ? ' ' + JSON.stringify(Object.fromEntries(Object.entries(win.status).filter(([k]) => k !== '2xx'))) : ''}  ` +
    `backlog ${backlog}${offline ? '  [OFFLINE]' : ''}`;
}

async function primary(v) {
  if (v.help || !v.target) { process.stdout.write(HELP); process.exit(v.help ? 0 : 2); }
  const o = buildOptions(v);
  const processes = Math.max(1, Math.min(64, Math.floor(Number(v.processes ?? Math.min(8, os.availableParallelism?.() ?? os.cpus().length)))));

  let mock = null, collectorUrl = v.target, sharedSecret = process.env.LOADTEST_SHARED_SECRET;
  if (v.target === 'mock') {
    sharedSecret = randomBytes(32).toString('base64');
    mock = await startMockCollector({ keyId: v['key-id'], sharedSecret, latencyMs: Number(v['mock-latency-ms']), failureRate: Number(v['mock-failure-rate']), trackEventIds: o.maxDevices <= 20_000 });
    collectorUrl = mock.url;
  } else {
    const host = new URL(collectorUrl).hostname;
    if (v['confirm-host'] !== host) {
      console.error(`Refusing to run: pass --confirm-host ${host} to confirm this is a TEST environment.\n` +
        'Every simulated event is written to Blob, SQL (and Sentinel if configured) in that environment.');
      process.exit(2);
    }
    if (!sharedSecret) { console.error('Set LOADTEST_SHARED_SECRET (base64 HMAC secret for --key-id).'); process.exit(2); }
  }

  console.log(`Plan: ${PROFILES[o.profile].describe(o)}`);
  console.log(`Target: ${v.target === 'mock' ? `mock collector (${collectorUrl})` : collectorUrl}  processes: ${processes}  duration: ${o.durationSec}s + up to ${o.drainSec}s drain`);
  console.log(`Per device: ${o.navPerHour} navigations/h, heartbeat ${o.heartbeatMs / 1000}s, flush ${o.flushMs / 1000}s${o.jitter ? ' (jittered)' : ''}, ${o.batchSize}x${o.maxBatchesPerFlush} events per flush`);

  const total = newWindow();
  const shardState = new Map();
  let windowAcc = newWindow(), windowStart = Date.now();
  const started = Date.now();

  const onWindow = (shard, m) => {
    mergeWindow(total, m.window);
    mergeWindow(windowAcc, m.window);
    shardState.set(shard, m);
    if (m.final) return;
    if (Date.now() - windowStart >= o.reportMs * 0.9 && shardState.size === processes) {
      windowAcc.spanMs = Date.now() - windowStart;
      const all = [...shardState.values()];
      console.log(line(Date.now() - started, windowAcc, all.reduce((s, x) => s + x.active, 0), all.reduce((s, x) => s + x.backlog, 0), all.some((x) => x.offline)));
      windowAcc = newWindow(); windowStart = Date.now();
    }
  };

  const opts = { ...o, collectorUrl, keyId: v['key-id'] };
  if (processes === 1) {
    const send = createTransport({ collectorUrl, keyId: v['key-id'], sharedSecret, extensionVersion: o.extensionVersion, maxSockets: o.maxInFlight, timeoutMs: o.timeoutMs });
    await runShard({ ...opts, shard: 0, shards: 1 }, send, (m) => onWindow(0, m));
  } else {
    const self = fileURLToPath(import.meta.url);
    await Promise.all(Array.from({ length: processes }, (_, shard) => new Promise((resolve, reject) => {
      const child = fork(self, ['--worker', JSON.stringify({ ...opts, shard, shards: processes })], { env: { ...process.env, LOADTEST_SHARED_SECRET: sharedSecret } });
      child.on('message', (m) => onWindow(shard, m));
      child.on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`worker ${shard} exited with ${code}`))));
    })));
  }

  const backlogLeft = [...shardState.values()].reduce((s, x) => s + x.backlog, 0);
  const extra = { backlogLeft };
  if (mock) extra.mock = { requests: mock.stats.requests, eventsAccepted: mock.stats.events, devicesSeen: mock.stats.devices.size, duplicateEvents: mock.stats.duplicateEvents, rejected: mock.stats.rejected };
  const s = summarize(total, o, (Date.now() - started) / 1000, extra);
  if (mock) await mock.close();

  console.log('\nSummary');
  console.log(JSON.stringify(s, null, 2));
  if (!s.checks.simulatorKeptUp) console.log('\nWARNING: the simulator fell behind its own schedule (client lag). Results understate the load: use more --processes or more machines.');
  console.log(s.passed ? '\nPASSED' : '\nFAILED');
  if (v.out) writeFileSync(v.out, JSON.stringify(s, null, 2));
  process.exit(s.passed ? 0 : 1);
}

async function worker(json) {
  const o = JSON.parse(json);
  const send = createTransport({ collectorUrl: o.collectorUrl, keyId: o.keyId, sharedSecret: process.env.LOADTEST_SHARED_SECRET, extensionVersion: o.extensionVersion, maxSockets: o.maxInFlight, timeoutMs: o.timeoutMs });
  await runShard(o, send, (m) => process.send(m));
  process.exit(0);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const v = parse(process.argv.slice(2));
  (v.worker ? worker(v.worker) : primary(v)).catch((err) => { console.error(err.message); process.exit(2); });
}
