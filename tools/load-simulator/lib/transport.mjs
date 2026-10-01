// Sends one batch exactly like extension/sw.js sendBatch(): same body, headers and HMAC signature
// (the signing code is the extension's own crypto.js).
import http from 'node:http';
import https from 'node:https';
import { getUnixEpochSeconds, signPayload } from '../../../extension/src/crypto.js';

export function createTransport({ collectorUrl, keyId, sharedSecret, extensionVersion, maxSockets, timeoutMs }) {
  const url = new URL(`${collectorUrl.replace(/\/+$/, '')}/api/v1/chrome/events/batch`);
  const lib = url.protocol === 'https:' ? https : http;
  const agent = new lib.Agent({ keepAlive: true, maxSockets, maxFreeSockets: maxSockets });

  return async function send(device, batch) {
    const body = JSON.stringify({ events: batch });
    const ts = getUnixEpochSeconds();
    const signature = await signPayload(sharedSecret, ts, body);
    const started = performance.now();
    return new Promise((resolve) => {
      const req = lib.request(url, {
        method: 'POST',
        agent,
        timeout: timeoutMs,
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(body),
          'X-Timestamp': String(ts),
          'X-Signature': signature,
          'X-Key-Id': keyId,
          'X-Device-Id': device.id.directoryDeviceId,
          'X-Client': 'chromebook-load-simulator',
          'X-Ext-Version': extensionVersion,
        },
      }, (res) => {
        res.resume(); // drain; only the status matters
        res.on('end', () => resolve({ ok: res.statusCode >= 200 && res.statusCode < 300, status: res.statusCode, ms: performance.now() - started }));
      });
      req.on('timeout', () => req.destroy(new Error('timeout')));
      req.on('error', (err) => resolve({ ok: false, status: err.message === 'timeout' ? 'timeout' : 'network', ms: performance.now() - started }));
      req.end(body);
    });
  };
}
