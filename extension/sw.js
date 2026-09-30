// MV3 service worker. Chrome stops it after ~30s idle and restarts it for each event, so:
// - all listeners are registered synchronously at top level;
// - nothing relies on in-memory state or setInterval (timers use chrome.alarms);
// - configuration and device details are (re)loaded lazily on each wake-up.
import { getConfig, invalidateConfig, isSendConfigured } from './src/config.js';
import { enqueue, peekBatch, removeEvents, takeDroppedCount } from './src/queue.js';
import { getUnixEpochSeconds, signPayload } from './src/crypto.js';
import { resolveSession, closeSession } from './src/session.js';
import { buildEvent } from './src/events.js';
import { getDeviceContext } from './src/device.js';

const FLUSH_ALARM = 'flush';
const HEARTBEAT_ALARM = 'heartbeat';
const HEARTBEAT_MINUTES = 5;
const MAX_BATCHES_PER_FLUSH = 20;

async function log(...args) {
  try {
    if ((await getConfig()).debug) console.log('[chromebook-poc]', ...args);
  } catch {
    // Logging must never throw.
  }
}

async function ensureAlarms({ reset = false } = {}) {
  const config = await getConfig();
  const flushMinutes = config.flushIntervalMs / 60_000;
  const existing = await chrome.alarms.get(FLUSH_ALARM);
  // Re-creating an alarm restarts its schedule, so only do it when missing, changed or explicitly requested.
  if (reset || !existing || existing.periodInMinutes !== flushMinutes) {
    await chrome.alarms.create(FLUSH_ALARM, { periodInMinutes: flushMinutes });
  }
  if (reset || !(await chrome.alarms.get(HEARTBEAT_ALARM))) {
    await chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: HEARTBEAT_MINUTES });
  }
}

function endReason(session) {
  if (session.userChanged) return 'user_changed';
  if (session.timedOut) return 'inactivity';
  return 'restart';
}

async function emitEvent(eventType, payload = {}) {
  const config = await getConfig();
  const device = await getDeviceContext();
  const session = await resolveSession(device.userEmail, config.inactivityTimeoutMinutes);
  const events = [];

  if (session.isNewSession && session.previousSessionId) {
    events.push(await buildEvent('SESSION_END', config, device, session.previousSessionId, { detail: endReason(session) }));
    if (session.userChanged) events.push(await buildEvent('LOGOUT', config, device, session.previousSessionId));
  }
  if (session.isNewSession) {
    events.push(await buildEvent('SESSION_START', config, device, session.sessionId));
    if (device.userEmail) events.push(await buildEvent('LOGIN', config, device, session.sessionId));
  }
  events.push(await buildEvent(eventType, config, device, session.sessionId, payload));

  await enqueue(events);
}

/** Sends one batch. Returns true if the batch left the queue (accepted or permanently rejected). */
async function sendBatch(config, batch) {
  const body = JSON.stringify({ events: batch });
  const ts = getUnixEpochSeconds();
  const signature = await signPayload(config.sharedSecret, ts, body);
  const device = await getDeviceContext();

  let response;
  try {
    response = await fetch(`${config.collectorUrl}/api/v1/chrome/events/batch`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Timestamp': String(ts),
        'X-Signature': signature,
        'X-Key-Id': config.keyId,
        'X-Device-Id': device.directoryDeviceId || 'unknown',
        'X-Client': 'chromebook-extension',
        'X-Ext-Version': chrome.runtime.getManifest().version
      },
      body
    });
  } catch (err) {
    await log('send failed (offline?)', err?.message);
    return false; // keep the batch; retry on the next alarm
  }

  if (response.ok) {
    await removeEvents(batch);
    return true;
  }

  // 400/413: the batch itself is invalid and will never be accepted; drop it so it cannot block the queue.
  // Anything else (401 key mismatch, 429, 5xx such as SQL resuming) is retried.
  if (response.status === 400 || response.status === 413) {
    await log('batch rejected permanently', response.status);
    await removeEvents(batch);
    return true;
  }
  await log('send failed, will retry', response.status);
  return false;
}

async function flush() {
  const config = await getConfig();
  if (!isSendConfigured(config)) {
    await log('collectorUrl/keyId/sharedSecret not set by policy; events stay queued');
    return;
  }
  for (let i = 0; i < MAX_BATCHES_PER_FLUSH; i += 1) {
    const batch = await peekBatch(config.batchSize);
    if (!batch.length) return;
    if (!(await sendBatch(config, batch))) return;
  }
}

async function heartbeat() {
  const dropped = await takeDroppedCount();
  await emitEvent('HEARTBEAT', dropped ? { detail: `queue_full_dropped=${dropped}` } : {});
  await flush();
}

function run(label, fn) {
  fn().catch((err) => log(`${label} failed`, err?.message ?? err));
}

chrome.runtime.onInstalled.addListener(() => run('install', async () => {
  await ensureAlarms({ reset: true });
  await heartbeat();
}));

// A browser start means a new sign-in: close whatever session was open before.
chrome.runtime.onStartup.addListener(() => run('startup', async () => {
  await closeSession();
  await ensureAlarms();
  await heartbeat();
}));

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === FLUSH_ALARM) run('flush', flush);
  if (alarm.name === HEARTBEAT_ALARM) run('heartbeat', heartbeat);
});

chrome.storage.onChanged.addListener((_changes, area) => {
  if (area !== 'managed') return;
  invalidateConfig();
  run('policy change', () => ensureAlarms());
});

chrome.history.onVisited.addListener((item) => run('navigation', () =>
  emitEvent('NAVIGATION', { url: item.url, title: item.title || null })));

chrome.downloads.onCreated.addListener((d) => run('download', () =>
  emitEvent('DOWNLOAD', {
    url: d.url,
    downloadFileName: d.filename || null,
    downloadState: d.state || null,
    downloadDanger: d.danger || null,
    downloadMime: d.mime || null
  })));

chrome.downloads.onChanged.addListener((delta) => {
  if (!delta.state && !delta.danger) return;
  run('download change', () =>
    emitEvent('DOWNLOAD', {
      downloadState: delta.state?.current || null,
      downloadDanger: delta.danger?.current || null
    }));
});

// Every wake-up: make sure the alarms exist (they survive restarts, but not an extension update that cleared them).
run('wake', () => ensureAlarms());
