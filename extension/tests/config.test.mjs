import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseConfig, isSendConfigured } from '../src/config.js';

test('defaults when policy is empty', () => {
  const c = parseConfig({});
  assert.equal(c.flushIntervalMs, 60_000);
  assert.equal(c.batchSize, 50);
  assert.equal(isSendConfigured(c), false);
});

test('trims values and strips trailing slashes from the URL', () => {
  const c = parseConfig({ collectorUrl: ' https://x.azurewebsites.net// ', keyId: ' KEY1 ', sharedSecret: 'abc=' });
  assert.equal(c.collectorUrl, 'https://x.azurewebsites.net');
  assert.equal(c.keyId, 'KEY1');
  assert.equal(isSendConfigured(c), true);
});

test('clamps the send interval to the 30s alarm minimum', () => {
  assert.equal(parseConfig({ flushIntervalMs: 5000 }).flushIntervalMs, 30_000);
  assert.equal(parseConfig({ flushIntervalMs: 9_999_999 }).flushIntervalMs, 600_000);
});
