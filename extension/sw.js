import { loadConfig } from './src/config.js';
import { enqueue, dequeueBatch, requeueFront, queueLength } from './src/queue.js';
import { getUnixEpochSeconds, signPayload } from './src/crypto.js';

const HEARTBEAT_INTERVAL_MS = 5 * 60 * 1000;
const MAX_BACKOFF_MS = 10 * 60 * 1000;
const BACKOFF_BASE_MS = 1_000;
const SESSION_KEY = 'sessionState';

let runtimeConfig = null;
let flushTimer = null;
let heartbeatTimer = null;
let nextBackoffMs = BACKOFF_BASE_MS;
let flushInProgress = false;

function log(...args) {
  if (runtimeConfig?.debug) {
    console.log('[telemetry-ext]', ...args);
  }
}

function getSafeConfigForLogs(config) {
  return {
    collectorUrl: config.collectorUrl,
    keyId: config.keyId,
    flushIntervalMs: config.flushIntervalMs,
    batchSize: config.batchSize,
    debug: config.debug,
    collectTitles: config.collectTitles
  };
}

function withJitter(ms, maxJitter = 5_000) {
  return ms + Math.floor(Math.random() * maxJitter);
}

async function getSessionId() {
  const today = new Date().toISOString().slice(0, 10);
  const stored = await chrome.storage.local.get({ [SESSION_KEY]: null });
  const session = stored[SESSION_KEY];
  if (session && session.date === today && session.id) {
    return session.id;
  }

  const id = crypto.randomUUID();
  await chrome.storage.local.set({ [SESSION_KEY]: { date: today, id } });
  return id;
}

async function getDeviceContext() {
  const context = {
    directoryDeviceId: null,
    serialNumber: null,
    userEmail: null
  };

  try {
    context.directoryDeviceId = await chrome.enterprise.deviceAttributes.getDirectoryDeviceId();
  } catch {
    // unavailable in non-managed contexts
  }

  try {
    context.serialNumber = await chrome.enterprise.deviceAttributes.getSerialNumber();
  } catch {
    // unavailable in non-managed contexts
  }

  try {
    const profile = await chrome.identity.getProfileUserInfo();
    context.userEmail = profile?.email || null;
  } catch {
    // identity might be restricted
  }

  return context;
}

async function enqueueEvent(type, payload = {}) {
  const event = {
    type,
    observedAt: new Date().toISOString(),
    payload,
    device: await getDeviceContext()
  };

  const len = await enqueue(event);
  log('event enqueued', type, 'queue length:', len);
}

async function postBatch(batch) {
  if (!runtimeConfig.collectorUrl || !runtimeConfig.keyId || !runtimeConfig.sharedSecret) {
    throw new Error('managed config incomplete');
  }

  const sessionId = await getSessionId();
  const timestamp = getUnixEpochSeconds();
  const rawBodyString = JSON.stringify({ events: batch });
  const signature = await signPayload(runtimeConfig.sharedSecret, timestamp, rawBodyString);
  const extVersion = chrome.runtime.getManifest().version;

  const deviceContext = await getDeviceContext();

  const response = await fetch(`${runtimeConfig.collectorUrl}/api/v1/chrome/events/batch`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-Timestamp': String(timestamp),
      'X-Signature': signature,
      'X-Key-Id': runtimeConfig.keyId,
      'X-Client': 'chromebook-extension',
      'X-Ext-Version': extVersion,
      'X-Session-Id': sessionId,
      'X-DeviceId': deviceContext.directoryDeviceId || 'unknown'
    },
    body: rawBodyString
  });

  if (!response.ok) {
    throw new Error(`collector response ${response.status}`);
  }
}

async function flushQueue() {
  if (flushInProgress) {
    return;
  }

  flushInProgress = true;
  try {
    const batch = await dequeueBatch(runtimeConfig.batchSize);
    if (batch.length === 0) {
      nextBackoffMs = BACKOFF_BASE_MS;
      return;
    }

    try {
      await postBatch(batch);
      nextBackoffMs = BACKOFF_BASE_MS;
      log('flush success', { sent: batch.length, remaining: await queueLength() });
    } catch (error) {
      await requeueFront(batch);
      nextBackoffMs = Math.min(nextBackoffMs * 2, MAX_BACKOFF_MS);
      const delay = withJitter(nextBackoffMs, 3_000);
      log('flush failed, will retry', String(error), 'retry in ms:', delay);
      setTimeout(() => {
        flushQueue().catch((err) => log('retry flush error', String(err)));
      }, delay);
    }
  } finally {
    flushInProgress = false;
  }
}

async function recordHeartbeat() {
  const extensionVersion = chrome.runtime.getManifest().version;
  await enqueueEvent('HEARTBEAT', {
    extensionVersion
  });
}

function scheduleFlush() {
  if (flushTimer) clearInterval(flushTimer);
  flushTimer = setInterval(() => {
    flushQueue().catch((error) => log('flush loop error', String(error)));
  }, withJitter(runtimeConfig.flushIntervalMs));
}

function scheduleHeartbeat() {
  if (heartbeatTimer) clearInterval(heartbeatTimer);
  heartbeatTimer = setInterval(() => {
    recordHeartbeat().catch((error) => log('heartbeat error', String(error)));
  }, HEARTBEAT_INTERVAL_MS);
}

async function init() {
  runtimeConfig = await loadConfig();
  log('config loaded', getSafeConfigForLogs(runtimeConfig));

  scheduleFlush();
  scheduleHeartbeat();
  await recordHeartbeat();
  await flushQueue();
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'managed') return;
  if (
    changes.collectorUrl ||
    changes.keyId ||
    changes.sharedSecret ||
    changes.flushIntervalMs ||
    changes.batchSize ||
    changes.debug ||
    changes.collectTitles
  ) {
    init().catch((error) => console.error('[telemetry-ext] reinit failed', error));
  }
});

chrome.history.onVisited.addListener((historyItem) => {
  enqueueEvent('NAVIGATION', {
    url: historyItem.url,
    title: runtimeConfig?.collectTitles ? historyItem.title || null : null,
    eventTimeUtc: historyItem.lastVisitTime ? new Date(historyItem.lastVisitTime).toISOString() : new Date().toISOString()
  }).catch((error) => log('history enqueue error', String(error)));
});

chrome.downloads.onCreated.addListener((item) => {
  enqueueEvent('DOWNLOAD', {
    eventAction: 'CREATED',
    id: item.id,
    url: item.url,
    filename: item.filename,
    mime: item.mime,
    totalBytes: item.totalBytes,
    danger: item.danger || null,
    state: item.state || null
  }).catch((error) => log('download create enqueue error', String(error)));
});

chrome.downloads.onChanged.addListener((delta) => {
  enqueueEvent('DOWNLOAD', {
    eventAction: 'CHANGED',
    id: delta.id,
    danger: delta.danger?.current ?? null,
    state: delta.state?.current ?? null,
    paused: delta.paused?.current ?? null,
    error: delta.error?.current ?? null,
    endTime: delta.endTime?.current ?? null
  }).catch((error) => log('download change enqueue error', String(error)));
});

chrome.runtime.onStartup.addListener(() => {
  init().catch((error) => console.error('[telemetry-ext] startup init failed', error));
});

chrome.runtime.onInstalled.addListener(() => {
  init().catch((error) => console.error('[telemetry-ext] install init failed', error));
});

init().catch((error) => console.error('[telemetry-ext] init failed', error));
