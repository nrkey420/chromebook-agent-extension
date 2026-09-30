import { withLock } from './queue.js';

const SESSION_STATE_KEY = 'sessionStateV2';

function nowIso() {
  return new Date().toISOString();
}

export function resolveSession(userEmail, inactivityTimeoutMinutes) {
  // Serialized with queue writes so two events arriving together cannot both start a new session.
  return withLock(() => resolveSessionUnlocked(userEmail, inactivityTimeoutMinutes));
}

async function resolveSessionUnlocked(userEmail, inactivityTimeoutMinutes) {
  const state = (await chrome.storage.local.get({ [SESSION_STATE_KEY]: null }))[SESSION_STATE_KEY];
  const now = Date.now();
  const timeoutMs = inactivityTimeoutMinutes * 60 * 1000;

  if (!state) {
    const sessionId = crypto.randomUUID();
    await chrome.storage.local.set({ [SESSION_STATE_KEY]: { sessionId, userEmail, lastSeenMs: now, active: true } });
    return { sessionId, isNewSession: true, previousSessionId: null, userChanged: false, timedOut: false };
  }

  const timedOut = now - (state.lastSeenMs || now) > timeoutMs;
  const userChanged = (state.userEmail || null) !== (userEmail || null);

  if (timedOut || userChanged || !state.active) {
    const sessionId = crypto.randomUUID();
    await chrome.storage.local.set({ [SESSION_STATE_KEY]: { sessionId, userEmail, lastSeenMs: now, active: true } });
    return {
      sessionId,
      isNewSession: true,
      previousSessionId: state.sessionId,
      userChanged,
      timedOut
    };
  }

  await chrome.storage.local.set({ [SESSION_STATE_KEY]: { ...state, userEmail, lastSeenMs: now, active: true } });
  return { sessionId: state.sessionId, isNewSession: false, previousSessionId: null, userChanged: false, timedOut: false };
}

export function closeSession() {
  return withLock(closeSessionUnlocked);
}

async function closeSessionUnlocked() {
  const state = (await chrome.storage.local.get({ [SESSION_STATE_KEY]: null }))[SESSION_STATE_KEY];
  if (!state) return null;
  await chrome.storage.local.set({ [SESSION_STATE_KEY]: { ...state, active: false, lastSeenMs: Date.now(), closedAtUtc: nowIso() } });
  return state.sessionId;
}
