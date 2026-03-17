# Session Attribution
Primary keys: sessionId + userEmail + directoryDeviceId + eventTimeUtc. Confidence: HIGH/MEDIUM/LOW. Session lifecycle: SESSION_START, LOGIN, HEARTBEAT(5m), LOGOUT, SESSION_END. Inactivity timeout default 20 minutes.
