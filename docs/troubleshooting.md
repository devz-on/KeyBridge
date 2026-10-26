# Troubleshooting

| Symptom | Action / interpretation |
| --- | --- |
| ADB absent | Install official Platform Tools, set its exact `adb.exe` path in Settings and Refresh. |
| No USB devices | Use a data-capable cable; USB debugging must be enabled. Bluetooth pairing is not an ADB USB connection. |
| Unauthorized | Unlock the phone and accept your computer's RSA debugging dialog locally. KeyBridge cannot accept it for you. |
| Offline | Reconnect the cable, open the app and refresh. Do not revoke all ADB keys unnecessarily. |
| Multiple phones | Select the intended serial/model. KeyBridge does not silently select one. |
| `get-devpath` reports unknown on Windows | The companion uses ADB's explicit USB selector or present USB PnP serial as a verified fallback. A genuinely unverified transport is rejected. |
| TLS EOF / connection refused | Start the visible Android bridge service. Verify the app is running, not just installed. ADB forwarding alone does not start an Android listener. |
| Certificate/pin mismatch | Reject the connection. If you deliberately reset/reinstalled Android, Forget the selected desktop pairing, revoke the old computer on the phone, then compare codes and re-pair. |
| Pairing times out | Keep KeyBridge visible on the phone, compare the same code and approve the requested computer. ADB authorization does not approve app pairing. |
| Fingerprint unavailable/rejected | Unlock the device and use an enrolled strong biometric. No desktop setting can bypass it. Enrollment changes may invalidate old credential keys; delete/re-register affected credentials using an alternate service login. |
| App was killed | Reopen Android KeyBridge, start its service and reconnect. Funtouch battery/background restrictions are respected. |
| CTAP cancelled/timed out | Start a new browser request. Pending approval is not reused. Cancellation can reserve a counter value without emitting a signature; counter gaps are expected. |
| RP/account missing | Check the credential vault and correct relying-party ID. Non-discoverable credentials require an allow list; unrelated passkey managers are not enumerated. |
| Unsupported ClientPIN/U2F | This release supports biometric CTAP2.0 ES256 only. It advertises those limits. A client policy requiring a PIN/U2F may not interoperate. |
| USB/IP driver missing | The server can run, but Windows will not enumerate a virtual security key until a verified compatible VHCI driver is installed. |
| Port 3240 busy | Stop another USB/IP server or another KeyBridge process. No LAN binding/port fallback is silently enabled. |
| Attach fails / access denied | Inspect the separately installed driver's health, signed client path and required privileges. Do not disable driver signature enforcement. |
| Attached but no Windows key | Driver import, HID PnP detection, FIDO usage and browser recognition are separate gates. Export diagnostics and record which gate fails. |
| Phone unplugged during signing | The desktop shuts down virtual transport; the phone cancels approval. Reconnect and initiate a new request. |
| “This security key doesn't look familiar” / no phone prompt | Cancel the stale Windows dialog. Confirm ADB shows the intended phone as `device`, start Android KeyBridge, connect in the companion, start USB/IP and Attach. Windows must show a present healthy `HID-compliant fido`. Use the same credential/RP as the browser page; clearing/reloading this diagnostic page loses its public test state while the phone credential remains. Older builds rejected Windows preflight; use the current APK and companion together. |
| Authentication asks for fingerprints twice | Windows can perform credential preflight before the final assertion. Each private-key use is deliberately biometric gated. Approve only your intended RP. Counter gaps or a first accepted counter of 2 are normal. |
| Norton-issued certificate or TLS failure | Keep pinning and security protections enabled. Use the matching current APK/companion with the mandatory KeyBridge protocol upgrade and ALPN. The application rejects inspection-proxy certificates; do not replace the phone pin with a Norton certificate. |

The diagnostic export excludes device serials, host names, RP IDs, account handles, certificate pins and CTAP data. Review any additional screenshots or manual logs before sharing them. The application is not FIDO certified and should not be tested against valuable production accounts.
