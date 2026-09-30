// Device and user identity. The enterprise APIs only work for force-installed extensions on managed ChromeOS
// devices with an affiliated user; otherwise they return an empty string or throw, and the field stays null.
let cached = null;

async function attempt(fn) {
  try {
    const value = await fn();
    return typeof value === 'string' && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

async function load() {
  const attrs = chrome.enterprise?.deviceAttributes;
  const [directoryDeviceId, serialNumber, assetId, annotatedLocation, hostname, userEmail] = await Promise.all([
    attempt(() => attrs.getDirectoryDeviceId()),
    attempt(() => attrs.getDeviceSerialNumber()),
    attempt(() => attrs.getDeviceAssetId()),
    attempt(() => attrs.getDeviceAnnotatedLocation()),
    attempt(() => attrs.getDeviceHostname()),
    // Requires the "identity.email" permission; empty without it.
    attempt(async () => (await chrome.identity.getProfileUserInfo({ accountStatus: 'ANY' }))?.email)
  ]);
  return { directoryDeviceId, serialNumber, assetId, annotatedLocation, hostname, userEmail };
}

/** Cached for the life of the service worker: these values do not change while a user is signed in. */
export async function getDeviceContext() {
  if (!cached) {
    cached = load();
  }
  return cached;
}
