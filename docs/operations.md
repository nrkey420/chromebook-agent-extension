# Operations
- Rotate HMAC keys by updating HMAC_KEYS__KEY1 and extension policy keyId/sharedSecret.
- Verify health: GET /api/health.
- Validate raw blobs, SQL rows, Sentinel ingest.
- E2E steps: deploy infra, set HMAC key, send signed test batch, configure DCR/DCE, init SQL, force-install extension, validate Power BI views.

## KQL examples
1. Recently visited domains by user.
2. Dangerous downloads.
3. New domains last 24h.
4. Device timeline by directoryDeviceId.
5. userEmail correlation across devices.
