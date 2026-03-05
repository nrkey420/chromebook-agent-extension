const DEFAULTS = {
  collectorUrl: '',
  keyId: '',
  sharedSecret: '',
  flushIntervalMs: 30_000,
  batchSize: 50,
  debug: false
};

function clampInt(value, fallback, min, max) {
  const parsed = Number.parseInt(value, 10);
  if (Number.isNaN(parsed)) return fallback;
  return Math.min(Math.max(parsed, min), max);
}

export async function loadConfig() {
  const managed = await chrome.storage.managed.get(Object.keys(DEFAULTS));

  return {
    collectorUrl: typeof managed.collectorUrl === 'string' ? managed.collectorUrl.replace(/\/+$/, '') : DEFAULTS.collectorUrl,
    keyId: typeof managed.keyId === 'string' ? managed.keyId : DEFAULTS.keyId,
    sharedSecret: typeof managed.sharedSecret === 'string' ? managed.sharedSecret : DEFAULTS.sharedSecret,
    flushIntervalMs: clampInt(managed.flushIntervalMs, DEFAULTS.flushIntervalMs, 5_000, 600_000),
    batchSize: clampInt(managed.batchSize, DEFAULTS.batchSize, 1, 500),
    debug: Boolean(managed.debug)
  };
}
