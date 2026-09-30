import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { installFakeChrome } from './fake-chrome.mjs';

const chrome = installFakeChrome();
const q = await import('../src/queue.js');

beforeEach(() => chrome.storage.local._reset());

const ev = (n) => ({ eventId: `id-${n}`, n });

test('concurrent enqueues are not lost', async () => {
  await Promise.all(Array.from({ length: 50 }, (_, i) => q.enqueue(ev(i))));
  assert.equal(await q.queueLength(), 50);
});

test('peek does not remove; remove takes only the given events', async () => {
  await q.enqueue([ev(1), ev(2), ev(3)]);
  const batch = await q.peekBatch(2);
  assert.deepEqual(batch.map((e) => e.n), [1, 2]);
  assert.equal(await q.queueLength(), 3);
  await q.enqueue(ev(4)); // arrives while the batch is in flight
  await q.removeEvents(batch);
  assert.deepEqual((await q.peekBatch(10)).map((e) => e.n), [3, 4]);
});

test('queue is capped, oldest dropped and counted', async () => {
  const extra = 5;
  await q.enqueue(Array.from({ length: q.MAX_QUEUE + extra }, (_, i) => ev(i)));
  assert.equal(await q.queueLength(), q.MAX_QUEUE);
  assert.equal((await q.peekBatch(1))[0].n, extra);
  assert.equal(await q.takeDroppedCount(), extra);
  assert.equal(await q.takeDroppedCount(), 0);
});
