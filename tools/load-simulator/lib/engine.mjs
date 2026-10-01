// Runs one shard of the simulated fleet: activates devices per the profile, generates their activity every tick, and
// flushes due devices with a cap on in-flight requests. Reports a metrics window every reportMs.
import { Device, makeRng } from './device.mjs';
import { PROFILES } from './profiles.mjs';
import { classify, newWindow, record } from './metrics.mjs';

export async function runShard(o, send, onWindow) {
  const profile = PROFILES[o.profile];
  const rng = makeRng(o.seed + o.shard * 7919);
  const total = o.maxDevices;
  const devices = [];
  for (let i = o.shard; i < total; i += o.shards) devices.push(new Device(i, o, makeRng(o.seed + i)));

  let w = newWindow();
  let inFlight = 0;
  let activated = 0;
  const startedAt = Date.now();
  let last = startedAt;
  let lastReport = startedAt;
  const endAt = startedAt + o.durationSec * 1000;
  const drainUntil = endAt + o.drainSec * 1000;

  const onResult = (device, batch, r) => {
    w.requests += 1;
    w.status[classify(r.status)] = (w.status[classify(r.status)] || 0) + 1;
    record(w.latency, r.ms);
    if (r.ok) {
      w.acceptedBatches += 1;
      w.acceptedEvents += batch.length;
      const at = Date.now();
      for (const e of batch) record(w.delivery, at - Date.parse(e.eventTimeUtc));
    } else if (r.status === 400 || r.status === 413) {
      w.droppedRejected += batch.length;
    }
  };

  const backlog = () => devices.reduce((s, d) => s + d.queue.length, 0);

  for (;;) {
    const now = Date.now();
    const elapsed = now - startedAt;
    const generating = now < endAt;
    if (!generating && (now >= drainUntil || backlog() === 0) && inFlight === 0) break;

    if (generating) {
      // Sign in devices up to the profile's level (devices are spread across shards by index).
      const target = profile.activeAt(elapsed, o);
      while (activated < devices.length && devices[activated].index < target) {
        const r = devices[activated].start(now);
        w.generatedEvents += r.added; w.droppedQueueFull += r.dropped;
        activated += 1;
      }
      const dt = now - last;
      for (let i = 0; i < activated; i += 1) {
        const r = devices[i].tick(now, dt);
        w.generatedEvents += r.added; w.droppedQueueFull += r.dropped;
      }
    }
    last = now;

    const offline = generating && profile.offlineAt(elapsed, o);
    if (offline) {
      // Collector unreachable: each device's send alarm still fires and fails, so it keeps its queue and tries again
      // one interval later. After the outage the devices reconnect spread over one interval, each with a backlog.
      for (let i = 0; i < activated; i += 1) {
        const d = devices[i];
        if (!d.flushing && now >= d.nextFlushAt) { d.nextFlushAt = now + o.flushMs; w.offlineAttempts += 1; }
      }
    } else {
      for (let i = 0; i < activated && inFlight < o.maxInFlight; i += 1) {
        const d = devices[i];
        if (d.flushing || now < d.nextFlushAt || (!generating && d.queue.length === 0)) continue;
        if (d.queue.length === 0) { d.nextFlushAt = now + o.flushMs; continue; }
        record(w.schedulerLag, Math.max(0, now - d.nextFlushAt));
        inFlight += 1;
        d.flush(send, now, onResult).finally(() => { inFlight -= 1; });
      }
    }

    if (now - lastReport >= o.reportMs) {
      onWindow({ window: w, elapsedMs: elapsed, active: activated, backlog: backlog(), inFlight, offline });
      w = newWindow();
      lastReport = now;
    }
    await new Promise((r) => setTimeout(r, o.tickMs));
  }
  onWindow({ window: w, elapsedMs: Date.now() - startedAt, active: activated, backlog: backlog(), inFlight, offline: false, final: true });
}
