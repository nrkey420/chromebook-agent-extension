# Self-hosted extension (no Chrome Web Store account)

This is the free, no-account route for the **pilot**: sign the extension yourself, host it on a GitHub
release, and force-install it from a custom URL in Google Admin. Switch to the Chrome Web Store for the
wide rollout (see the last section).

The manifest already declares the update URL this route needs:
`update_url` → `https://github.com/nrkey420/chromebook-agent-extension/releases/latest/download/update.xml`
(the Chrome Web Store ignores that field, so the same manifest works for both routes).

## One-time: create a signing key and the `.crx`

Chrome signs the package for you; no developer account, no fee.

1. Get the extension files: download the **chrome-web-store-v<version>** artifact from the
   **Build extension package** workflow (or the `extension-v<version>` release) and unzip it, so you have a
   folder containing `manifest.json`, `sw.js`, `src/`, `policy/`, `icons/`. Or use the `extension/` folder
   from a clone of `main`.
2. On any desktop Chrome: `chrome://extensions` → turn on **Developer mode** → **Pack extension**.
   - **Extension root directory:** the folder from step 1.
   - **Private key file:** leave empty the first time.
3. Chrome creates two files next to the folder:
   - `<folder>.crx` — the signed extension.
   - `<folder>.pem` — the **private key**. Store it somewhere safe and backed up (a password manager or a
     restricted share). **Reuse the same `.pem` for every future build** so the extension ID never changes.
4. Get the **extension ID**: drag the `.crx` onto `chrome://extensions`, or load the unpacked folder; the
   32-character ID is shown on its card. It is derived from the key, so it stays constant as long as you
   reuse the `.pem`.

For later updates, run **Pack extension** again and this time set **Private key file** to your saved `.pem`.

## Host the `.crx` and `update.xml` on a GitHub release

The `.crx` is signed locally (CI cannot sign it), so you upload it by hand.

1. Rename the signed file to **`chromebook-agent.crx`** (matches the URL below).
2. Create `update.xml` with this content, filling in your extension ID and matching the manifest `version`:

   ```xml
   <?xml version='1.0' encoding='UTF-8'?>
   <gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
     <app appid='YOUR_EXTENSION_ID'>
       <updatecheck codebase='https://github.com/nrkey420/chromebook-agent-extension/releases/latest/download/chromebook-agent.crx'
                    version='0.3.0' />
     </app>
   </gupdate>
   ```

3. Publish a release (GitHub → **Releases** → **Draft a new release**), tag it `extension-v<version>`
   (e.g. `extension-v0.3.0`), and attach **both** `chromebook-agent.crx` and `update.xml`.
   - The **Build extension package** workflow (run with *Create a GitHub release*) can create this release
     with the zip and images; you then edit it and add the two files above. Or create the release by hand.
   - `releases/latest/download/<asset>` always points at the newest release, so the manifest `update_url`
     and the `codebase` URL stay stable across versions.

Both URLs are public HTTPS, which is all ChromeOS needs. Nothing else in the repo is exposed.

## Force-install in Google Admin

Google Admin → **Devices → Chrome → Apps & extensions → Users & browsers** → select the **pilot OU** →
**+** → **Add Chrome app or extension by ID**:
- **Extension ID:** your 32-character ID.
- **From a custom URL / Installation source:** the update URL
  `https://github.com/nrkey420/chromebook-agent-extension/releases/latest/download/update.xml`.
- Set **Installation policy: Force install**.

Then set the extension policy (collector URL, `keyId`, `sharedSecret`) exactly as in
`set-extension-policy.md` — that part is identical for both routes.

## Updating the pilot

1. Bump `version` in `extension/manifest.json` (Chrome refuses a re-used version), merge to `main`.
2. Repack the `.crx` with your saved `.pem`, update `version` in `update.xml`, and publish a **new**
   `extension-v<version>` release with both files attached.
3. Managed Chromebooks pick it up on their next update check (within hours; `chrome://extensions` →
   **Update** forces it).

## Switching to the Chrome Web Store later

When the $5 developer account is approved:
1. Upload the same zip to the Web Store, set visibility to your domain, submit for review.
2. The Web Store assigns a **new extension ID** (it re-signs with its own key — the ID will differ from the
   self-hosted one). Note it from the dashboard after approval.
3. In Google Admin, change the pilot/rollout force-install entry to the **new Web Store ID** (remove the
   custom-URL entry). Chromebooks then install the store version; the old self-hosted one is removed.
4. You can stop publishing the `.crx`/`update.xml` releases at that point. The `update_url` in the manifest
   is harmless for a Web Store item.

Because attribution is per device/session, events already collected stay in the backend; only the on-device
extension install is replaced.
