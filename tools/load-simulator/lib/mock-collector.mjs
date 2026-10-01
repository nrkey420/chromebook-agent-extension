// In-process stand-in for the collector, for dry runs and the simulator's own tests. It checks requests the way the
// real collector does (IngestBatchFunction + HmacAuth + BatchSchemaValidator), independently of the extension code.
import { createHmac, timingSafeEqual } from 'node:crypto';
import http from 'node:http';

export async function startMockCollector({ keyId, sharedSecret, latencyMs = 5, failureRate = 0, rng = Math.random, trackEventIds = true } = {}) {
  const key = Buffer.from(sharedSecret, 'base64');
  const stats = { requests: 0, accepted: 0, events: 0, rejected: {}, duplicateEvents: 0, devices: new Set() };
  const seen = trackEventIds ? new Set() : null;
  const reject = (res, code, why) => { stats.rejected[why] = (stats.rejected[why] || 0) + 1; res.writeHead(code).end(JSON.stringify({ error: why })); };

  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => setTimeout(() => {
      stats.requests += 1;
      if (req.method !== 'POST' || req.url !== '/api/v1/chrome/events/batch') return reject(res, 404, 'route');
      const ts = req.headers['x-timestamp'], sig = req.headers['x-signature'], kid = req.headers['x-key-id'];
      if (!ts || !sig || !kid) return reject(res, 400, 'headers');
      if (kid !== keyId) return reject(res, 401, 'key');
      if (Math.abs(Date.now() / 1000 - Number(ts)) > 300) return reject(res, 401, 'skew');
      const raw = Buffer.concat(chunks);
      const expected = createHmac('sha256', key).update(`${ts}\n${raw.toString('utf8')}`).digest('base64');
      if (expected.length !== sig.length || !timingSafeEqual(Buffer.from(expected), Buffer.from(sig))) return reject(res, 401, 'signature');
      let batch;
      try { batch = JSON.parse(raw); } catch { return reject(res, 400, 'json'); }
      if (!Array.isArray(batch.events) || batch.events.length === 0) return reject(res, 400, 'empty');
      if (batch.events.some((e) => !e.eventType || !e.eventTimeUtc)) return reject(res, 400, 'schema');
      if (rng() < failureRate) return reject(res, 503, 'injected-failure');
      stats.accepted += 1;
      stats.events += batch.events.length;
      for (const e of batch.events) {
        stats.devices.add(e.directoryDeviceId);
        if (seen) { if (seen.has(e.eventId)) stats.duplicateEvents += 1; else seen.add(e.eventId); }
      }
      res.writeHead(202, { 'Content-Type': 'application/json' }).end(JSON.stringify({ accepted: batch.events.length }));
    }, latencyMs));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { url: `http://127.0.0.1:${server.address().port}`, stats, close: () => new Promise((r) => { server.closeAllConnections?.(); server.close(r); }) };
}
