import { getNetworkDetails } from './network.js';

export async function buildEvent(eventType, config, device, sessionId, payload = {}) {
  const network = await getNetworkDetails();
  const { title, ...rest } = payload;

  return {
    eventId: crypto.randomUUID(), // lets the collector drop duplicates when a batch is retried
    eventType,
    eventTimeUtc: new Date().toISOString(),
    sessionId,
    userEmail: device.userEmail,
    directoryDeviceId: device.directoryDeviceId,
    serialNumber: device.serialNumber,
    assetId: device.assetId,
    annotatedLocation: device.annotatedLocation,
    hostname: device.hostname,
    internalIp: network.internalIp,
    internalIpv6: network.internalIpv6,
    macAddress: network.macAddress,
    extensionVersion: chrome.runtime.getManifest().version,
    ...rest,
    title: config.collectTitles ? title ?? null : null
  };
}
