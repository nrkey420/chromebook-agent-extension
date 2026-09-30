import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { installFakeChrome, settle } from './fake-chrome.mjs';

const SECRET = Buffer.from('test-secret-key-0123456789abcdef').toString('base64');
const MANAGED = { collectorUrl: 'https://collector.azurewebsites.net/', keyId: 'KEY1', sharedSecret: SECRET };

const chrome = installFakeChrome({ managed: MANAGED });
let responses = [];
let requests = [];
globalThis.fetch = async (url, init) => {
  requests.push({ url, init, body: JSON.parse(init.body) });
  const next = responses.shift() ?? { status: 202 };
  if (next.throw) throw new TypeError('Failed to fetch');
  return { ok: next.status >= 200 && next.status < 300, status: next.status };
};

await import('../sw.js');
const queue = await import('../src/queue.js');

async function fire(event, ...args) {
  await Promise.all(event.dispatch(...args));
  await settle();
}
const alarm = (name) => fire(chrome.alarms.onAlarm, { name });
const allSent = () => requests.flatMap((r) => r.body.events);

beforeEach(async () => {
  chrome.storage.local._reset();
  chrome.storage.managed._reset(MANAGED);
  chrome.storage.onChanged.dispatch({}, 'managed');
  responses = [];
  requests = [];
  await settle();
});

test('install creates alarms and sends a signed first batch', async () => {
  await fire(chrome.runtime.onInstalled);

  assert.equal(chrome.alarms._all.get('flush').periodInMinutes, 1);
  assert.equal(chrome.alarms._all.get('heartbeat').periodInMinutes, 5);

  assert.equal(requests.length, 1);
  const { url, init, body } = requests[0];
  assert.equal(url, 'https://collector.azurewebsites.net/api/v1/chrome/events/batch');
  assert.deepEqual(body.events.map((e) => e.eventType), ['SESSION_START', 'LOGIN', 'HEARTBEAT']);

  const e = body.events[2];
  assert.match(e.eventId, /^[0-9a-f-]{36}$/);
  assert.equal(e.userEmail, 'user@example.org');
  assert.equal(e.serialNumber, 'SN123');
  assert.equal(e.directoryDeviceId, 'device-1');
  assert.equal(e.internalIp, '10.1.2.3');
  assert.equal(e.macAddress, 'aa:bb:cc:dd:ee:ff');

  // Same signing scheme the collector verifies: base64(HMACSHA256(key, timestamp + "\n" + body)).
  const h = init.headers;
  const expected = createHmac('sha256', Buffer.from(SECRET, 'base64')).update(`${h['X-Timestamp']}\n${init.body}`).digest('base64');
  assert.equal(h['X-Signature'], expected);
  assert.equal(h['X-Key-Id'], 'KEY1');
  assert.equal(h['X-Device-Id'], 'device-1');

  assert.equal(await queue.queueLength(), 0);
});

test('events survive an offline send and go out on the next flush', async () => {
  responses = [{ throw: true }];
  await fire(chrome.runtime.onInstalled);
  assert.equal(await queue.queueLength(), 3);

  await alarm('flush');
  assert.equal(await queue.queueLength(), 0);
  assert.equal(requests.length, 2);
  assert.deepEqual(requests[1].body.events.map((e) => e.eventId), requests[0].body.events.map((e) => e.eventId));
});

test('5xx and 401 are retried; 400 is dropped so it cannot block the queue', async () => {
  for (const status of [503, 401]) {
    responses = [{ status }];
    await alarm('heartbeat');
    assert.ok((await queue.queueLength()) > 0, `kept after ${status}`);
    await alarm('flush');
    assert.equal(await queue.queueLength(), 0);
  }
  responses = [{ status: 400 }];
  await alarm('heartbeat');
  assert.equal(await queue.queueLength(), 0);
});

test('navigation is queued and sent on the next flush, in the same session', async () => {
  await fire(chrome.runtime.onInstalled);
  requests = [];

  await fire(chrome.history.onVisited, { url: 'https://example.com/a', title: 'A' });
  await fire(chrome.history.onVisited, { url: 'https://example.com/b', title: 'B' });
  assert.equal(requests.length, 0);
  await alarm('flush');

  const sent = allSent();
  assert.deepEqual(sent.map((e) => e.eventType), ['NAVIGATION', 'NAVIGATION']);
  assert.equal(sent[0].title, 'A');
  const sessionIds = new Set(sent.map((e) => e.sessionId));
  assert.equal(sessionIds.size, 1, 'no new session between events');
});

test('browser startup ends the previous session and starts a new one', async () => {
  await fire(chrome.runtime.onInstalled);
  const firstSession = requests[0].body.events[0].sessionId;
  requests = [];

  await fire(chrome.runtime.onStartup);
  const types = allSent().map((e) => [e.eventType, e.sessionId === firstSession ? 'old' : 'new', e.detail ?? null]);
  assert.deepEqual(types.slice(0, 3), [['SESSION_END', 'old', 'restart'], ['SESSION_START', 'new', null], ['LOGIN', 'new', null]]);
});

test('nothing is sent until policy provides the collector settings', async () => {
  chrome.storage.managed._reset({});
  chrome.storage.onChanged.dispatch({}, 'managed');
  await settle();

  await alarm('heartbeat');
  assert.equal(requests.length, 0);
  assert.ok((await queue.queueLength()) > 0);

  chrome.storage.managed._reset(MANAGED);
  chrome.storage.onChanged.dispatch({}, 'managed');
  await alarm('flush');
  assert.equal(await queue.queueLength(), 0);
});
