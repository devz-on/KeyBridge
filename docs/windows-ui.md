# Windows companion

KeyBridge is a native WPF application with four primary destinations. Session state and actions come from `KeyBridge.Core.BridgeController`; credential private keys remain on Android.

| Destination | Behavior |
| --- | --- |
| Connection | Select an authorized phone, install the bundled APK, connect, compare/approve pairing, disconnect or forget a phone, inspect driver state and enable the virtual key. Enable starts the USB/IP backend and attaches it in one action; signed-driver attachment is enabled by default. |
| Checks | Reports completed and remaining requirements: Windows, ADB, signed driver/client, authorized phone, reachable phone app, pairing, strong biometric availability, backend and Windows HID detection. Unknown phone readiness is incomplete. Refresh forces a new signature/driver check. |
| Activity | Up to 100 recent session events. It is an in-memory connection log, not a persistent authentication audit database. |
| Settings | Appearance, reduced motion, tray, notifications, startup and reconnection. Tool paths and wireless opt-in are under Advanced connection tools. Diagnostics and About open as detail screens with a Settings return button. |

The former Home, Setup, Devices and Security key pages are combined into Connection. Checks is restored as a separate readiness page. Diagnostics/About are no longer sidebar tabs. The header Refresh action remains available. Errors appear in a dismissible banner; long operations show progress. Pairing cancellation remains available during pending approval.

## Connect and activate

1. Run the all-in-one Windows setup. It includes private ADB tools and the verified upstream driver installer; see [installer.md](installer.md).
2. Open KeyBridge on Android and tap Start bridge. In Windows Connection, select the intended authorized phone. Install the APK first if necessary.
3. Connect. On first use, compare all 64 pairing fingerprint characters, confirm in Windows and approve locally on the phone.
4. Attachment is enabled by default. Select Enable security key. A paired session, verified client signature, driver health and actual HID enumeration are required. Windows reports Security key ready only after its HID check.
5. Approve passkey requests on the phone. Disconnect stops the session and detaches the virtual key.

A backend listener or healthy HID does not itself prove browser authentication. The offline diagnostic verifies the actual assertion signature and WebAuthn bindings. The earlier real browser test passed; see [verification.md](verification.md).

## Settings and local data

`%LOCALAPPDATA%\KeyBridge\ui.json` stores appearance/tray preferences. `settings.json` stores tool paths, transport options, the last selected routing alias and pinned public certificate fingerprints. Valid custom paths are preserved; missing paths fall back to private bundled ADB and the standard installed USB/IP client. Neither file contains credential private keys.

The current-user Personal certificate store holds the non-exportable Windows CNG **transport** identity (`KeyBridge local transport identity v1`, key name `KeyBridge.Transport.<identifier>`). It is not a FIDO credential. Optional startup uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KeyBridge` and is off unless enabled. Redacted diagnostic export omits device names/serials, certificate pins, RP IDs, accounts, CTAP payloads and executable paths.

Closing normally disposes the bridge. With Keep in tray enabled, close hides it; the visible tray menu has Open, Stop bridge and Exit. Optional reconnection never approves requests or automatically attaches a driver. A per-user mutex prevents duplicate instances; an additional session mutex lets setup detect a running companion before overwriting it.

## Verification scope

The 0.1.2 native Release build and self-contained publish passed. The standard-size Connection and Checks layouts were inspected with the shared icon and default attachment option. The physical USB phone reconnected using the existing pinned identity, the updated phone reported strong biometrics/TEE storage, the virtual key attached, and Checks reached Ready to use with Windows FIDO HID detection.

This readiness test did not perform a new signing ceremony. Earlier real browser registration/assertion evidence is recorded separately. Other DPI/text sizes, complete accessibility coverage and fresh-PC driver installation remain outstanding.
