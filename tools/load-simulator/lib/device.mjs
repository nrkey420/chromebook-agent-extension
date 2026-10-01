// One simulated Chromebook running the extension (v0.3 behaviour): a session with SESSION_START/LOGIN, heartbeats,
// navigations and downloads; a local queue flushed on an interval in batches; retries on failure.
// All identifiers are tagged "loadtest" so the data can be found and deleted (infra/scripts/sql/delete-load-test-data.sql).
import { randomUUID } from 'node:crypto';

const SITES = [
  ['classroom.google.com', ['/c/', '/u/0/h', '/w/'], 'Google Classroom'],
  ['docs.google.com', ['/document/d/', '/presentation/d/', '/spreadsheets/d/'], 'Google Docs'],
  ['www.youtube.com', ['/watch?v=', '/results?search_query='], 'YouTube'],
  ['www.google.com', ['/search?q='], 'Google Search'],
  ['en.wikipedia.org', ['/wiki/'], 'Wikipedia'],
  ['www.khanacademy.org', ['/math/', '/science/'], 'Khan Academy'],
  ['quizlet.com', ['/', '/create-set'], 'Quizlet'],
  ['www.desmos.com', ['/calculator', '/scientific'], 'Desmos'],
  ['www.coolmathgames.com', ['/0-'], 'Cool Math Games'],
  ['mail.google.com', ['/mail/u/0/#inbox'], 'Gmail'],
];
const WORDS = ['photosynthesis', 'fractions', 'civil war', 'volcano', 'poem', 'essay outline', 'cell division', 'algebra', 'map of europe', 'water cycle'];
const pick = (rng, list) => list[Math.floor(rng() * list.length)];

// Small deterministic PRNG so a run with the same --seed produces the same traffic shape.
export function makeRng(seed) {
  let s = seed >>> 0 || 1;
  return () => { s ^= s << 13; s >>>= 0; s ^= s >>> 17; s ^= s << 5; s >>>= 0; return s / 4294967296; };
}

export function deviceIdentity(index) {
  const n = String(index).padStart(6, '0');
  return {
    directoryDeviceId: `loadtest-${n}`,
    serialNumber: `LOADTEST${n}`,
    assetId: `LT-${n}`,
    annotatedLocation: `Load test room ${(index % 200) + 1}`,
    hostname: `loadtest-${n}`,
    userEmail: `loadtest-${n}@loadtest.invalid`,
    internalIp: `10.${200 + ((index >> 16) & 0x1f)}.${(index >> 8) & 0xff}.${index & 0xff}`,
    macAddress: (0x02_00_00_00_00_00 + index).toString(16).padStart(12, '0'),
  };
}

export class Device {
  constructor(index, options, rng) {
    this.index = index;
    this.id = deviceIdentity(index + (options.deviceOffset || 0));
    this.o = options;
    this.rng = rng;
    this.queue = [];
    this.active = false;
    this.sessionId = null;
    this.nextFlushAt = Infinity;
    this.nextHeartbeatAt = Infinity;
    this.flushing = false;
  }

  event(eventType, payload = {}, now = Date.now()) {
    return {
      eventId: randomUUID(),
      eventType,
      eventTimeUtc: new Date(now).toISOString(),
      sessionId: this.sessionId,
      userEmail: this.id.userEmail,
      directoryDeviceId: this.id.directoryDeviceId,
      serialNumber: this.id.serialNumber,
      assetId: this.id.assetId,
      annotatedLocation: this.id.annotatedLocation,
      hostname: this.id.hostname,
      internalIp: this.id.internalIp,
      internalIpv6: null,
      macAddress: this.id.macAddress,
      extensionVersion: this.o.extensionVersion,
      title: null,
      ...payload,
    };
  }

  enqueue(events) {
    this.queue.push(...events);
    const overflow = this.queue.length - this.o.maxQueue;
    if (overflow > 0) { this.queue.splice(0, overflow); return { added: events.length, dropped: overflow }; }
    return { added: events.length, dropped: 0 };
  }

  // Browser start / sign-in: new session, LOGIN, an immediate heartbeat and flush (the extension's onStartup).
  start(now) {
    this.active = true;
    this.sessionId = randomUUID();
    const r = this.enqueue([this.event('SESSION_START', {}, now), this.event('LOGIN', {}, now), this.event('HEARTBEAT', {}, now)]);
    this.nextHeartbeatAt = now + this.o.heartbeatMs;
    // The real extension flushes on startup; with jitter (proposed fix) the first flush is spread over one interval.
    this.nextFlushAt = now + (this.o.jitter ? this.rng() * this.o.flushMs : 0);
    return r;
  }

  // Activity generated in the last dtMs: Poisson-ish navigations, occasional downloads, heartbeats.
  tick(now, dtMs) {
    if (!this.active) return { added: 0, dropped: 0 };
    const events = [];
    const expected = (this.o.navPerHour * dtMs) / 3_600_000;
    let n = Math.floor(expected);
    if (this.rng() < expected - n) n += 1;
    for (let i = 0; i < n; i += 1) {
      if (this.rng() < this.o.downloadShare) {
        const file = `worksheet-${Math.floor(this.rng() * 1000)}.pdf`;
        events.push(this.event('DOWNLOAD', { url: `https://docs.google.com/uc?id=${randomUUID()}`, downloadFileName: file,
          downloadMime: 'application/pdf', downloadState: 'in_progress', downloadDanger: 'safe' }, now));
      } else {
        const [host, paths, label] = pick(this.rng, SITES);
        const path = pick(this.rng, paths);
        const q = encodeURIComponent(pick(this.rng, WORDS));
        const tail = path.includes('?') ? q : `${randomUUID().slice(0, 12)}`;
        events.push(this.event('NAVIGATION', { url: `https://${host}${path}${tail}`, title: `${decodeURIComponent(q)} - ${label}` }, now));
      }
    }
    while (now >= this.nextHeartbeatAt) {
      events.push(this.event('HEARTBEAT', {}, now));
      this.nextHeartbeatAt += this.o.heartbeatMs;
    }
    return events.length ? this.enqueue(events) : { added: 0, dropped: 0 };
  }

  // Up to maxBatchesPerFlush batches, oldest first; stop at the first failure (events stay queued), like sw.js flush().
  async flush(send, now, onResult) {
    this.flushing = true;
    try {
      for (let i = 0; i < this.o.maxBatchesPerFlush && this.queue.length; i += 1) {
        const batch = this.queue.slice(0, this.o.batchSize);
        const result = await send(this, batch);
        onResult(this, batch, result);
        if (result.ok || result.status === 400 || result.status === 413) {
          this.queue.splice(0, batch.length);
          if (!result.ok) continue; // permanently rejected: dropped, carry on
        } else {
          break; // retried on the next flush
        }
      }
    } finally {
      this.flushing = false;
      this.nextFlushAt = now + this.o.flushMs;
    }
  }
}
