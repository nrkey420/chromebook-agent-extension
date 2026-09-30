# Force-install the extension and set its policy (Google Admin)

Start with a **test OU** containing one Chromebook and one test account.

## 1. Publish the extension

Package it with `bash extension/tools/pack.sh` (or `extension/tools/pack.ps1`), which produces
`extension/dist/chromebook-activity-extension-v<version>.zip`.

The simplest way to force-install on managed Chromebooks is a **private Chrome Web Store item**:
1. Register a Chrome Web Store developer account with an account in your Google Workspace domain.
2. Upload the zip and set **Visibility → Private** to your domain.
3. After review, note the **extension ID** shown in the developer dashboard.

(Self-hosting a `.crx` with an update manifest also works for ChromeOS but needs a signing key and a public
URL for the update XML; the Web Store route avoids both.)

The manifest allows the extension to reach `https://*.azurewebsites.net/*`, which covers the Function App's
default hostname. If the collector moves to a custom domain, package with
`COLLECTOR_HOST_PERMISSION='https://collector.example.org/*' bash extension/tools/pack.sh`.

## 2. Force-install it

Google Admin → **Devices → Chrome → Apps & extensions → Users & browsers** → select the test OU →
add the extension by ID → set **Installation policy: Force install**.

## 3. Set the extension policy

In the same panel, select the extension and paste this into **Policy for extensions**. Google Admin expects
each setting wrapped in `{"Value": ...}`.

```json
{
  "collectorUrl": { "Value": "https://<functionHostname>" },
  "keyId": { "Value": "KEY1" },
  "sharedSecret": { "Value": "<HmacKey1 from Key Vault>" },
  "flushIntervalMs": { "Value": 60000 },
  "batchSize": { "Value": 50 },
  "inactivityTimeoutMinutes": { "Value": 20 },
  "collectTitles": { "Value": true },
  "debug": { "Value": false }
}
```

- `functionHostname`: from the Deploy to Azure workflow's "Deploy infrastructure" step or run summary.
- `sharedSecret`: `az keyvault secret show --vault-name <keyVaultName> -n HmacKey1 --query value -o tsv`.
  It must match exactly, or every batch is rejected with 401.
- `flushIntervalMs` has a minimum of 30000 (Chrome alarms cannot fire more often).
- Until `collectorUrl`, `keyId` and `sharedSecret` are set, the extension queues events locally and sends nothing.

## 4. Settings the extension depends on

For the test OU:
- **Enrollment and sign-in:** the device must be enrolled in your domain and the user must be a domain
  account. The device serial number, directory device ID, local IP and MAC come from enterprise APIs that only
  answer for force-installed extensions and *affiliated* users (same domain as the device).
- **Browser history must be saved** (do not set "Browser history: never save"). Page visits are recorded from
  Chrome's history events.
- **Incognito mode: disallow** and **Guest mode: disable**. The extension does not run in either.

## 5. Check it on the Chromebook

1. Sign in with the test account and browse a few pages.
2. In `chrome://policy`, the extension's policy values should be listed. Note that anyone on the device
   can read them there, including `sharedSecret`; per-device keys are a later hardening step.
3. Within a couple of minutes, `dbo.vw_Devices` in SQL should show the device with `ExtLastSeenUtc` set,
   and `dbo.vw_LoginHistory` a `SESSION_START` for the test account.
4. For troubleshooting, set `debug` to `true` and open `chrome://extensions` → the extension →
   *service worker* to see its log.
