import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { Device, deviceIdentity, makeRng } from '../lib/device.mjs';
import { PROFILES } from '../lib/profiles.mjs';
import { mergeWindow, newHistogram, newWindow, percentile, record } from '../lib/metrics.mjs';
import { startMockCollector } from '../lib/mock-collector.mjs';
import { createTransport } from '../lib/transport.mjs';
import { runShard } from '../lib/engine.mjs';
import { buildOptions } from '../simulator.mjs';

const SIM = fileURLToPath(new URL('../simulator.mjs', import.meta.url));
const baseArgs = (over = {}) => ({
  profile: 'baseline', devices: '1000', duration: undefined, 'ramp-sec': '300', steps: '1000,5000', 'step-sec': '900',
  'bell-sec': '600', 'outage-sec': '1800', 'nav-per-hour': '77', 'download-share': '0.005', 'flush-sec': '60',
  'heartbeat-sec': '300', 'batch-size': '50', 'max-batches': '20', jitter: false, 'max-in-flight': '256', 'timeout-sec': '30',
  'drain-sec': '120', 'report-sec': '10', 'tick-ms': '250', seed: '1', 'device-offset': '0', 'max-p95-ms': '2000', 'max-error-rate': '0.01', ...over,
});

function deviceOptions(over = {}) {
  return { navPerHour: 0, downloadShare: 0, heartbeatMs: 300_000, flushMs: 60_000, batchSize: 50, maxBatchesPerFlush: 20,
    maxQueue: 20_000, jitter: false, extensionVersion: '0.0.0-loadtest', ...over };
}

test('device identities are tagged as load test data', () => {
  const id = deviceIdentity(42);
  assert.equal(id.directoryDeviceId, 'loadtest-000042');
  assert.match(id.serialNumber, /^LOADTEST/);
  assert.match(id.userEmail, /@loadtest\.invalid$/);
});

test('--device-offset moves a machine onto its own device range', () => {
  const d = new Device(5, { ...deviceOptions(), deviceOffset: 100_000 }, makeRng(1));
  assert.equal(d.id.directoryDeviceId, 'loadtest-100005');
  assert.equal(buildOptions(baseArgs({ 'device-offset': '25000' })).deviceOffset, 25000);
});

test('sign-in queues SESSION_START, LOGIN and HEARTBEAT and flushes at once (extension onStartup)', () => {
  const d = new Device(1, deviceOptions(), makeRng(1));
  d.start(1_000);
  assert.deepEqual(d.queue.map((e) => e.eventType), ['SESSION_START', 'LOGIN', 'HEARTBEAT']);
  assert.ok(d.queue.every((e) => e.sessionId === d.sessionId && e.eventId && e.eventTimeUtc));
  assert.equal(d.nextFlushAt, 1_000);
});

test('jitter spreads the first flush over one interval', () => {
  const d = new Device(1, deviceOptions({ jitter: true }), makeRng(7));
  d.start(0);
  assert.ok(d.nextFlushAt >= 0 && d.nextFlushAt < 60_000 && d.nextFlushAt > 0);
});

test('heartbeats every interval and navigations at the configured rate', () => {
  const d = new Device(1, deviceOptions({ navPerHour: 3600, heartbeatMs: 10_000 }), makeRng(3));
  d.start(0);
  d.queue.length = 0;
  for (let t = 250; t <= 60_000; t += 250) d.tick(t, 250);
  const types = d.queue.map((e) => e.eventType);
  assert.equal(types.filter((t) => t === 'HEARTBEAT').length, 6);
  const navs = types.filter((t) => t === 'NAVIGATION').length;
  assert.ok(navs > 40 && navs < 80, `expected ~60 navigations, got ${navs}`);
});

test('queue is capped like the extension (oldest dropped)', () => {
  const d = new Device(1, deviceOptions({ maxQueue: 5 }), makeRng(1));
  d.start(0);
  const r = d.enqueue([1, 2, 3, 4].map((i) => d.event('NAVIGATION', { url: `https://x/${i}` }, i)));
  assert.equal(d.queue.length, 5);
  assert.equal(r.dropped, 2);
  assert.equal(d.queue[0].eventType, 'HEARTBEAT');
});

test('flush: accepted batches leave the queue, 400 drops the batch and continues, 5xx stops and keeps events', async () => {
  const d = new Device(1, deviceOptions({ batchSize: 2 }), makeRng(1));
  d.start(0);
  d.enqueue([d.event('NAVIGATION', {}, 1), d.event('NAVIGATION', {}, 2), d.event('NAVIGATION', {}, 3)]);
  const responses = [{ ok: true, status: 202 }, { ok: false, status: 400 }, { ok: false, status: 503 }];
  const sent = [];
  await d.flush(async (_dev, batch) => { sent.push(batch.length); return responses.shift(); }, 0, () => {});
  assert.deepEqual(sent, [2, 2, 2]);
  assert.equal(d.queue.length, 2, 'the batch that got 503 stays queued for the next flush');
  assert.equal(d.nextFlushAt, 60_000);
});

test('profiles: activation, steps and the recovery outage window', () => {
  const o = { devices: 1000, rampSec: 100, steps: [10, 50], stepSec: 60, bellSec: 10, outageSec: 30 };
  assert.equal(PROFILES.baseline.activeAt(50_000, o), 500);
  assert.equal(PROFILES.baseline.activeAt(500_000, o), 1000);
  assert.equal(PROFILES.bell.activeAt(5_000, o), 500);
  assert.equal(PROFILES.step.activeAt(30_000, o), 10);
  assert.equal(PROFILES.step.activeAt(90_000, o), 50);
  assert.equal(PROFILES.step.activeAt(999_000, o), 50);
  assert.equal(PROFILES.step.duration(o), 120);
  assert.equal(PROFILES.recovery.offlineAt(159_000, o), false);
  assert.equal(PROFILES.recovery.offlineAt(160_000, o), true);
  assert.equal(PROFILES.recovery.offlineAt(190_000, o), false);
});

test('metrics: percentiles and merging', () => {
  const a = newHistogram(), b = newHistogram();
  for (let i = 1; i <= 90; i += 1) record(a, 5);
  for (let i = 1; i <= 10; i += 1) record(b, 1000);
  const w1 = newWindow(), w2 = newWindow();
  w1.latency = a; w2.latency = b; w1.status['2xx'] = 90; w2.status['503'] = 10;
  mergeWindow(w1, w2);
  assert.equal(percentile(w1.latency, 50), 5);
  assert.ok(percentile(w1.latency, 95) >= 1000);
  assert.deepEqual(w1.status, { '2xx': 90, 503: 10 });
});

test('options: validation and recovery duration guard', () => {
  assert.throws(() => buildOptions(baseArgs({ profile: 'nope' })), /Unknown --profile/);
  assert.throws(() => buildOptions(baseArgs({ devices: '-1' })), /non-negative/);
  assert.throws(() => buildOptions(baseArgs({ profile: 'recovery', duration: '100' })), /recovery needs --duration/);
  assert.equal(buildOptions(baseArgs({ profile: 'step' })).durationSec, 1800);
  assert.equal(buildOptions(baseArgs({ profile: 'step' })).maxDevices, 5000);
});

test('end to end against the mock collector: every event arrives exactly once, signatures verify, despite 503s', async () => {
  const secret = randomBytes(32).toString('base64');
  let calls = 0;
  const mock = await startMockCollector({ keyId: 'KEY1', sharedSecret: secret, latencyMs: 1, failureRate: 0.2, rng: () => ((calls += 1) % 5 === 0 ? 0 : 1) });
  try {
    const o = { ...buildOptions(baseArgs({ devices: '40', duration: '6', 'ramp-sec': '1', 'flush-sec': '1', 'heartbeat-sec': '2',
      'nav-per-hour': '7200', 'drain-sec': '10', 'tick-ms': '50', 'report-sec': '1' })), shard: 0, shards: 1 };
    const send = createTransport({ collectorUrl: mock.url, keyId: 'KEY1', sharedSecret: secret, extensionVersion: o.extensionVersion, maxSockets: 32, timeoutMs: 5000 });
    const total = newWindow();
    let last;
    await runShard(o, send, (m) => { mergeWindow(total, m.window); last = m; });

    assert.equal(last.backlog, 0);
    assert.ok(total.generatedEvents > 300, `generated ${total.generatedEvents}`);
    assert.equal(total.acceptedEvents, total.generatedEvents);
    assert.equal(mock.stats.events, total.generatedEvents);
    assert.equal(mock.stats.duplicateEvents, 0);
    assert.equal(mock.stats.devices.size, 40);
    assert.ok(mock.stats.rejected['injected-failure'] > 0, 'some requests failed and were retried');
    assert.equal(mock.stats.rejected.signature, undefined, 'every signature verified');
  } finally {
    await mock.close();
  }
});

test('CLI refuses a real target without --confirm-host, and without the secret', () => {
  const run = (args, env = {}) => spawnSync(process.execPath, [SIM, ...args], { encoding: 'utf8', env: { ...process.env, LOADTEST_SHARED_SECRET: '', ...env } });
  const noConfirm = run(['--target', 'https://collector-test.example.org', '--devices', '1']);
  assert.equal(noConfirm.status, 2);
  assert.match(noConfirm.stderr, /--confirm-host collector-test\.example\.org/);
  const noSecret = run(['--target', 'https://collector-test.example.org', '--confirm-host', 'collector-test.example.org', '--devices', '1']);
  assert.equal(noSecret.status, 2);
  assert.match(noSecret.stderr, /LOADTEST_SHARED_SECRET/);
});

test('CLI dry run against the mock passes and reports a summary', () => {
  const r = spawnSync(process.execPath, [SIM, '--target', 'mock', '--devices', '30', '--duration', '4', '--ramp-sec', '1', '--flush-sec', '1',
    '--heartbeat-sec', '2', '--nav-per-hour', '3600', '--drain-sec', '5', '--report-sec', '1', '--processes', '2'], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stdout, /PASSED/);
  const summary = JSON.parse(r.stdout.slice(r.stdout.indexOf('{'), r.stdout.lastIndexOf('}') + 1));
  assert.equal(summary.eventsAccepted, summary.eventsGenerated);
  assert.equal(summary.mock.devicesSeen, 30);
  assert.equal(summary.backlogLeft, 0);
});
