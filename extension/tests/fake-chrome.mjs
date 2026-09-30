// Minimal in-memory stand-in for the chrome.* APIs the extension uses.
function event() {
  const listeners = [];
  return {
    listeners,
    addListener: (fn) => listeners.push(fn),
    dispatch: (...args) => listeners.map((fn) => fn(...args))
  };
}

function area(initial = {}) {
  let data = structuredClone(initial);
  return {
    _data: () => data,
    _reset: (next = {}) => { data = structuredClone(next); },
    async get(keys) {
      if (keys && typeof keys === 'object' && !Array.isArray(keys)) {
        return Object.fromEntries(Object.entries(keys).map(([k, d]) => [k, k in data ? structuredClone(data[k]) : d]));
      }
      const list = Array.isArray(keys) ? keys : Object.keys(data);
      return Object.fromEntries(list.filter((k) => k in data).map((k) => [k, structuredClone(data[k])]));
    },
    async set(values) {
      await new Promise((r) => setTimeout(r, 0)); // make interleaving possible, like the real async API
      Object.assign(data, structuredClone(values));
    }
  };
}

export function installFakeChrome({ managed = {}, device = {}, email = 'user@example.org', network = {} } = {}) {
  const alarms = new Map();
  const chrome = {
    storage: { local: area(), managed: area(managed), onChanged: event() },
    alarms: {
      async create(name, info) { alarms.set(name, { name, ...info }); },
      async get(name) { return alarms.get(name); },
      onAlarm: event(),
      _all: alarms
    },
    runtime: { getManifest: () => ({ version: '0.3.0-test' }), onInstalled: event(), onStartup: event() },
    identity: { async getProfileUserInfo() { return { email }; } },
    enterprise: {
      deviceAttributes: {
        async getDirectoryDeviceId() { return device.directoryDeviceId ?? 'device-1'; },
        async getDeviceSerialNumber() { return device.serialNumber ?? 'SN123'; },
        async getDeviceAssetId() { return device.assetId ?? ''; },
        async getDeviceAnnotatedLocation() { return device.annotatedLocation ?? ''; },
        async getDeviceHostname() { return device.hostname ?? ''; }
      },
      networkingAttributes: {
        async getNetworkDetails() { return { ipv4: '10.1.2.3', ipv6: null, macAddress: 'aa:bb:cc:dd:ee:ff', ...network }; }
      }
    },
    history: { onVisited: event() },
    downloads: { onCreated: event(), onChanged: event() }
  };
  globalThis.chrome = chrome;
  return chrome;
}

/** Waits for pending listener work (they run fire-and-forget). */
export async function settle(ms = 30) {
  await new Promise((r) => setTimeout(r, ms));
}
