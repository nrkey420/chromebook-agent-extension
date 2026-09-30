const DEFAULTS = {
  collectorUrl: '',
  keyId: '',
  sharedSecret: '',
  flushIntervalMs: 60_000,
  batchSize: 50,
  debug: false,
  collectTitles: true,
  inactivityTimeoutMinutes: 20
};

let cached = null;

function clampInt(value, fallback, min, max) {
  const parsed = Number.parseInt(value, 10);
  if (Number.isNaN(parsed)) return fallback;
  return Math.min(Math.max(parsed, min), max);
}

export function parseConfig(managed) {
  return {
    collectorUrl: typeof managed.collectorUrl === 'string' ? managed.collectorUrl.trim().replace(/\/+$/, '') : DEFAULTS.collectorUrl,
    keyId: typeof managed.keyId === 'string' ? managed.keyId.trim() : DEFAULTS.keyId,
    sharedSecret: typeof managed.sharedSecret === 'string' ? managed.sharedSecret.trim() : DEFAULTS.sharedSecret,
    // Chrome alarms fire at most every 30 seconds.
    flushIntervalMs: clampInt(managed.flushIntervalMs, DEFAULTS.flushIntervalMs, 30_000, 600_000),
    batchSize: clampInt(managed.batchSize, DEFAULTS.batchSize, 1, 500),
    debug: Boolean(managed.debug),
    collectTitles: managed.collectTitles === undefined ? DEFAULTS.collectTitles : Boolean(managed.collectTitles),
    inactivityTimeoutMinutes: clampInt(managed.inactivityTimeoutMinutes, DEFAULTS.inactivityTimeoutMinutes, 5, 240)
  };
}

export function isSendConfigured(config) {
  return Boolean(config.collectorUrl && config.keyId && config.sharedSecret);
}

/**
 * Managed policy, cached for the life of the service worker. Chrome stops the worker when idle, so this
 * is re-read on every wake-up; invalidateConfig() is called when the policy changes.
 */
export async function getConfig() {
  if (!cached) {
    cached = chrome.storage.managed.get(Object.keys(DEFAULTS)).then(parseConfig).catch((err) => {
      cached = null;
      throw err;
    });
  }
  return cached;
}

export function invalidateConfig() {
  cached = null;
}
