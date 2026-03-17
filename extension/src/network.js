export async function getInternalIpContext() {
  // Privacy boundary: this is best-effort metadata enrichment only.
  const fallback = { internalIp: null, internalIpConfidence: 'UNAVAILABLE' };

  try {
    const rtc = new RTCPeerConnection({ iceServers: [] });
    rtc.createDataChannel('ip');

    const ip = await new Promise((resolve) => {
      const timeout = setTimeout(() => resolve(null), 1500);
      rtc.onicecandidate = (event) => {
        if (!event?.candidate?.candidate) return;
        const match = event.candidate.candidate.match(/(\d+\.\d+\.\d+\.\d+)/);
        if (match) {
          clearTimeout(timeout);
          resolve(match[1]);
        }
      };

      rtc.createOffer()
        .then((offer) => rtc.setLocalDescription(offer))
        .catch(() => resolve(null));
    });

    rtc.close();

    if (!ip) return fallback;
    if (ip.startsWith('10.') || ip.startsWith('192.168.') || /^172\.(1[6-9]|2\d|3[0-1])\./.test(ip)) {
      return { internalIp: ip, internalIpConfidence: 'HIGH' };
    }

    return { internalIp: ip, internalIpConfidence: 'LOW' };
  } catch {
    return fallback;
  }
}
