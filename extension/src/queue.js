const QUEUE_KEY = 'queue';

async function readQueue() {
  const state = await chrome.storage.local.get({ [QUEUE_KEY]: [] });
  return Array.isArray(state[QUEUE_KEY]) ? state[QUEUE_KEY] : [];
}

async function writeQueue(queue) {
  await chrome.storage.local.set({ [QUEUE_KEY]: queue });
}

export async function enqueue(event) {
  const queue = await readQueue();
  queue.push(event);
  await writeQueue(queue);
  return queue.length;
}

export async function dequeueBatch(batchSize) {
  const queue = await readQueue();
  const batch = queue.slice(0, batchSize);
  const remaining = queue.slice(batch.length);
  await writeQueue(remaining);
  return batch;
}

export async function requeueFront(events) {
  if (!events.length) return;
  const queue = await readQueue();
  await writeQueue([...events, ...queue]);
}

export async function queueLength() {
  const queue = await readQueue();
  return queue.length;
}
