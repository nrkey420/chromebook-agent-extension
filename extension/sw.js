import { loadConfig } from './src/config.js';
import { enqueue, dequeueBatch, requeueFront } from './src/queue.js';
import { getUnixEpochSeconds, signPayload } from './src/crypto.js';
import { resolveSession, closeSession } from './src/session.js';
import { buildEvent } from './src/events.js';

const HEARTBEAT_INTERVAL_MS = 5 * 60 * 1000;
let runtimeConfig;
let flushTimer;
let heartbeatTimer;

function log(...args) {
  if (runtimeConfig?.debug) console.log('[chromebook-poc]', ...args);
}

async function getDeviceContext() {
  const context = { directoryDeviceId: null, serialNumber: null, userEmail: null, orgUnit: null, school: null };
  try { context.directoryDeviceId = await chrome.enterprise.deviceAttributes.getDirectoryDeviceId(); } catch {}
  try { context.serialNumber = await chrome.enterprise.deviceAttributes.getSerialNumber(); } catch {}
  try {
    const profile = await chrome.identity.getProfileUserInfo();
    context.userEmail = profile?.email || null;
  } catch {}
  return context;
}

async function emitEvent(eventType, payload = {}) {
  const device = await getDeviceContext();
  const s = await resolveSession(device.userEmail, runtimeConfig.inactivityTimeoutMinutes);

  if (s.isNewSession && s.previousSessionId) {
    await enqueue(await buildEvent('SESSION_END', runtimeConfig, device, s.previousSessionId, {}));
    if (s.userChanged) await enqueue(await buildEvent('LOGOUT', runtimeConfig, device, s.previousSessionId, {}));
  }

  if (s.isNewSession) {
    await enqueue(await buildEvent('SESSION_START', runtimeConfig, device, s.sessionId, {}));
    if (device.userEmail) await enqueue(await buildEvent('LOGIN', runtimeConfig, device, s.sessionId, {}));
  }

  await enqueue(await buildEvent(eventType, runtimeConfig, device, s.sessionId, payload));
}

async function flush() {
  const batch = await dequeueBatch(runtimeConfig.batchSize);
  if (!batch.length) return;
  const body = JSON.stringify({ events: batch });
  const ts = getUnixEpochSeconds();
  const sig = await signPayload(runtimeConfig.sharedSecret, ts, body);
  const response = await fetch(`${runtimeConfig.collectorUrl}/api/v1/chrome/events/batch`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-Timestamp': String(ts),
      'X-Signature': sig,
      'X-Key-Id': runtimeConfig.keyId,
      'X-Session-Id': batch[0]?.sessionId || crypto.randomUUID(),
      'X-Client': 'chromebook-extension',
      'X-Ext-Version': chrome.runtime.getManifest().version
    },
    body
  });

  if (!response.ok) {
    await requeueFront(batch);
    log('flush failed', response.status);
  }
}

async function init() {
  runtimeConfig = await loadConfig();
  clearInterval(flushTimer);
  clearInterval(heartbeatTimer);
  flushTimer = setInterval(() => flush().catch((e) => log(e.message)), runtimeConfig.flushIntervalMs);
  heartbeatTimer = setInterval(() => emitEvent('HEARTBEAT').catch((e) => log(e.message)), HEARTBEAT_INTERVAL_MS);
  await emitEvent('HEARTBEAT');
  await flush();
}

chrome.runtime.onStartup.addListener(() => { init().catch(console.error); });
chrome.runtime.onInstalled.addListener(() => { init().catch(console.error); });
chrome.history.onVisited.addListener((h) => emitEvent('NAVIGATION', { url: h.url, title: h.title || null }).catch(log));
chrome.downloads.onCreated.addListener((d) => emitEvent('DOWNLOAD', { downloadFileName: d.filename, downloadState: d.state || null, downloadDanger: d.danger || null, downloadMime: d.mime || null, url: d.url }).catch(log));
chrome.downloads.onChanged.addListener((d) => emitEvent('DOWNLOAD', { downloadState: d.state?.current || null, downloadDanger: d.danger?.current || null }).catch(log));
chrome.runtime.onSuspend.addListener(async () => {
  const s = await closeSession();
  if (s) await enqueue(await buildEvent('SESSION_END', runtimeConfig, await getDeviceContext(), s, {}));
  await flush();
});
