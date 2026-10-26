# Start KeyBridge

This is the self-contained Windows x64 development package. Extract the ZIP completely; no .NET installation is required to run the bundled programs.

1. Install the matching Android APK, open KeyBridge on the phone, and tap **Start bridge**. Existing credentials/pairings survive an `adb install -r` update using the same debug signing identity; do not uninstall the phone app.
2. Run `app/KeyBridge.exe`. In Settings, point ADB to your official Platform Tools. For authorized wireless debugging, enable its explicit option. Set the signed USB/IP client path, normally `C:\Program Files\USBip\usbip.exe`.
3. Open Connection, select your online phone and Connect. An existing pinned phone is recognized even when its ADB routing name changes. First use requires comparing all 64 fingerprint characters on both devices and approving locally.
4. Stay on Connection and click **Enable security key**. It starts the backend and attaches the device in one action. Windows must report a healthy **HID-compliant fido**. Use Disconnect to stop and detach.
5. Run `Run-WebAuthn-Diagnostic.cmd`, then open **http://localhost:8787** in Chrome. Register a test credential, then authenticate. Complete Windows' security-key flow and approve the phone fingerprints. Windows may preflight the credential first, requiring an additional fresh approval.

The portable ZIP includes the offline diagnostic and documentation. For private ADB tools, the phone APK and the original signed USB/IP driver installer in one executable, use **KeyBridge-0.1.2-Setup-x64.exe**. Its driver step remains interactive and elevated; setup never restarts Windows automatically. No browser extension is required.

The separately verified signed USB/IP driver was installed with the user's consent and exercised successfully on the development PC. New computers require their own explicit installation and compatibility checks. See `docs/driver-verification.md` and `docs/driver-component-verification.md`.

The development test verified real Chrome registration and authentication through Windows FIDO HID, USB/IP, authorized wireless ADB and Android Keystore. The assertion signature passed with counter 2. ADB loss removed the virtual key. See `docs/verification.md` for remaining untested cases and capability limits. This release is not FIDO certified or independently security audited.

`Install.ps1` is an optional per-user copy/shortcut installer. If your PowerShell policy blocks it, use the portable executable; do not weaken policy. `Uninstall.ps1` handles the per-user copy and optional preferences. Exit the bridge first and revoke the desktop in Android before deliberately removing its identity. Uninstalling/resetting Android destroys its private keys and cannot be undone.
