function decodeBase64(input) {
  const binary = atob(input);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function encodeBase64(bytes) {
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

export async function signPayload(sharedSecretBase64, timestamp, rawBodyString) {
  const keyBytes = decodeBase64(sharedSecretBase64);
  const cryptoKey = await crypto.subtle.importKey(
    'raw',
    keyBytes,
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign']
  );

  const signingString = `${timestamp}\n${rawBodyString}`;
  const signingBytes = new TextEncoder().encode(signingString);
  const signature = await crypto.subtle.sign('HMAC', cryptoKey, signingBytes);

  return encodeBase64(new Uint8Array(signature));
}

export function getUnixEpochSeconds() {
  return Math.floor(Date.now() / 1000);
}
