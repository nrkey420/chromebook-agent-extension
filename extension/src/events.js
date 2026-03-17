import { getInternalIpContext } from './network.js';

export async function buildEvent(eventType, runtimeConfig, deviceContext, sessionId, payload = {}) {
  const ipContext = await getInternalIpContext();

  return {
    eventType,
    eventTimeUtc: new Date().toISOString(),
    sessionId,
    userEmail: deviceContext.userEmail || null,
    directoryDeviceId: deviceContext.directoryDeviceId || null,
    serialNumber: deviceContext.serialNumber || null,
    internalIp: ipContext.internalIp,
    internalIpConfidence: ipContext.internalIpConfidence,
    publicIp: null,
    extensionVersion: chrome.runtime.getManifest().version,
    orgUnit: deviceContext.orgUnit || null,
    school: deviceContext.school || null,
    ...payload,
    title: runtimeConfig.collectTitles ? payload.title ?? null : null
  };
}
