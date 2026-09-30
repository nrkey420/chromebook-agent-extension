// Local network details from chrome.enterprise.networkingAttributes (force-installed extension, affiliated
// user, ChromeOS only). Cached briefly because the value is attached to every event.
const CACHE_MS = 60_000;
let cache = { at: 0, value: null };

const EMPTY = { internalIp: null, internalIpv6: null, macAddress: null };

export async function getNetworkDetails() {
  const now = Date.now();
  if (cache.value && now - cache.at < CACHE_MS) return cache.value;

  let value = EMPTY;
  try {
    const details = await chrome.enterprise.networkingAttributes.getNetworkDetails();
    value = {
      internalIp: details?.ipv4 || null,
      internalIpv6: details?.ipv6 || null,
      macAddress: details?.macAddress || null
    };
  } catch {
    // Not affiliated, not on a network, or not ChromeOS.
  }

  cache = { at: now, value };
  return value;
}
