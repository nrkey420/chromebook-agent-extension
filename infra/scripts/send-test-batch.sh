#!/usr/bin/env bash
set -euo pipefail
: "${COLLECTOR_URL:?}"; : "${KEY_ID:?}"; : "${SHARED_SECRET_B64:?}"
TS=$(date +%s)
BODY='{"events":[{"eventType":"SESSION_START","eventTimeUtc":"'$(date -u +%FT%TZ)'","sessionId":"6c70737a-2df7-4e5d-94a6-cf6f2ee0f4bf","userEmail":"student@district.org","directoryDeviceId":"abc123-directory-device-id","serialNumber":"SN123456","url":"https://example.com","title":"Example","extensionVersion":"0.2.0"}]}'
SIG=$(printf "%s\n%s" "$TS" "$BODY" | openssl dgst -sha256 -hmac "$(echo "$SHARED_SECRET_B64" | base64 -d)" -binary | base64)
curl -sS -X POST "$COLLECTOR_URL/api/v1/chrome/events/batch" -H "Content-Type: application/json" -H "X-Key-Id: $KEY_ID" -H "X-Timestamp: $TS" -H "X-Signature: $SIG" -d "$BODY"
