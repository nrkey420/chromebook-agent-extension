// Persistent event queue in chrome.storage.local.
// - Every read-modify-write runs through one lock, so events arriving together cannot overwrite each other.
// - Sending peeks at a batch and only removes it after the collector accepts it, so a failed or interrupted
//   send (offline, worker stopped mid-request) never loses events. The collector de-duplicates by eventId.
// - The queue is capped; when full the oldest events are dropped and counted.
export const QUEUE_KEY = 'queue';
export const DROPPED_KEY = 'droppedEvents';
export const MAX_QUEUE = 20_000;

let lock = Promise.resolve();

export function withLock(fn) {
  const run = lock.then(fn, fn);
  lock = run.catch(() => {});
  return run;
}

async function readQueue() {
  const state = await chrome.storage.local.get({ [QUEUE_KEY]: [] });
  return Array.isArray(state[QUEUE_KEY]) ? state[QUEUE_KEY] : [];
}

export function enqueue(events) {
  const list = Array.isArray(events) ? events : [events];
  return withLock(async () => {
    const queue = await readQueue();
    queue.push(...list);
    const overflow = queue.length - MAX_QUEUE;
    const update = { [QUEUE_KEY]: overflow > 0 ? queue.slice(overflow) : queue };
    if (overflow > 0) {
      const state = await chrome.storage.local.get({ [DROPPED_KEY]: 0 });
      update[DROPPED_KEY] = state[DROPPED_KEY] + overflow;
    }
    await chrome.storage.local.set(update);
    return update[QUEUE_KEY].length;
  });
}

/** Returns up to batchSize of the oldest events without removing them. */
export function peekBatch(batchSize) {
  return withLock(async () => (await readQueue()).slice(0, batchSize));
}

/** Removes the given events (by eventId) after the collector has accepted or permanently rejected them. */
export function removeEvents(events) {
  const ids = new Set(events.map((e) => e.eventId));
  return withLock(async () => {
    const queue = await readQueue();
    const remaining = queue.filter((e) => !ids.has(e.eventId));
    await chrome.storage.local.set({ [QUEUE_KEY]: remaining });
    return remaining.length;
  });
}

export function queueLength() {
  return withLock(async () => (await readQueue()).length);
}

/** Returns and resets the count of events dropped because the queue was full. */
export function takeDroppedCount() {
  return withLock(async () => {
    const state = await chrome.storage.local.get({ [DROPPED_KEY]: 0 });
    if (state[DROPPED_KEY]) await chrome.storage.local.set({ [DROPPED_KEY]: 0 });
    return state[DROPPED_KEY];
  });
}
