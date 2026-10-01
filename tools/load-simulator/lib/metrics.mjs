// Mergeable metrics: counters plus fixed log-scale histograms, so worker processes can send snapshots to the primary
// and memory stays constant however long the test runs.

// Bucket upper bounds in ms: 1..10 by 1, then ~10% steps up to 120 s.
export const BOUNDS = (() => {
  const b = [];
  for (let v = 1; v <= 10; v += 1) b.push(v);
  let v = 10;
  while (v < 120_000) { v = Math.ceil(v * 1.1); b.push(v); }
  b.push(Infinity);
  return b;
})();

export function newHistogram() {
  return { counts: new Array(BOUNDS.length).fill(0), n: 0, sum: 0, max: 0 };
}

export function record(h, ms) {
  let lo = 0, hi = BOUNDS.length - 1;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (ms <= BOUNDS[mid]) hi = mid; else lo = mid + 1; }
  h.counts[lo] += 1; h.n += 1; h.sum += ms; if (ms > h.max) h.max = ms;
}

export function mergeHistogram(into, from) {
  for (let i = 0; i < into.counts.length; i += 1) into.counts[i] += from.counts[i];
  into.n += from.n; into.sum += from.sum; into.max = Math.max(into.max, from.max);
}

export function percentile(h, p) {
  if (!h.n) return null;
  const target = Math.ceil((p / 100) * h.n);
  let seen = 0;
  for (let i = 0; i < h.counts.length; i += 1) {
    seen += h.counts[i];
    if (seen >= target) return Number.isFinite(BOUNDS[i]) ? BOUNDS[i] : h.max;
  }
  return h.max;
}

export function newWindow() {
  return {
    requests: 0, offlineAttempts: 0, acceptedBatches: 0, acceptedEvents: 0, generatedEvents: 0,
    status: {},            // HTTP status (or "network"/"timeout") -> count
    droppedRejected: 0,    // events dropped after a 400/413, like the extension
    droppedQueueFull: 0,   // events dropped because a device queue hit its cap
    latency: newHistogram(),      // request latency
    delivery: newHistogram(),     // event age when the collector accepted it (event time -> accepted)
    schedulerLag: newHistogram(), // how late flushes started vs. schedule (client saturation)
  };
}

export function mergeWindow(into, from) {
  for (const k of ['requests', 'offlineAttempts', 'acceptedBatches', 'acceptedEvents', 'generatedEvents', 'droppedRejected', 'droppedQueueFull']) into[k] += from[k];
  for (const [code, n] of Object.entries(from.status)) into.status[code] = (into.status[code] || 0) + n;
  mergeHistogram(into.latency, from.latency);
  mergeHistogram(into.delivery, from.delivery);
  mergeHistogram(into.schedulerLag, from.schedulerLag);
}

export function classify(status) {
  if (typeof status !== 'number') return status;
  if (status >= 200 && status < 300) return '2xx';
  return String(status);
}

export function errorCount(w) {
  return Object.entries(w.status).filter(([k]) => k !== '2xx').reduce((s, [, n]) => s + n, 0);
}
