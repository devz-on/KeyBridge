# Android application

## Navigation in 0.1.1

The bottom bar has Home, Vault, Activity and Settings. Home owns bridge control; the duplicate Connection screen and More menu have been removed. Security, paired computers, diagnostics and About are detail screens under Settings. System Back and the toolbar arrow use the same chronological history, including tab visits and revisited tabs. Selecting the current tab adds no history entry. Back exits the activity only after returning through that history to Home. History and the selected credential alias use saved instance state.

On the physical phone, the 0.1.1 debug APK updated successfully over authorized USB ADB without uninstalling. The navigation check passed Home → Vault → Activity → Settings → Paired computers, followed by Back to Settings → Activity → Vault → Home. Re-selecting Vault did not add an extra Back step. The app remained open through the tab/detail returns. This UI check did not request credential signing or biometric approval and did not rerun instrumentation against the existing vault.

KeyBridge targets Android 11/API 30 or newer. It needs no root, bootloader unlock, kernel modification or custom ROM. Build with JDK 21, Android SDK platform 36, and Gradle 8.13:

```powershell
cd android
.\gradlew.bat :core:test :app:assembleDebug
```

The debug APK is `android/app/build/outputs/apk/debug/app-debug.apk`. A release must use the release variant and the maintainer's own signing key; never distribute the debug signing identity as a production release identity. Keep an independent account recovery method while using this experimental authenticator. Keystore private keys have no export/backup path.

## Security behavior

Every credential has its own Android Keystore P-256 key. The provider requires a fresh strong biometric for every private-key operation, invalidates credential keys when biometric enrollment changes, and requires an unlocked device. It requests StrongBox when available and reports the actual Keystore security level; StrongBox is not assumed. Assertion signing uses the exact `Signature` object supplied through `BiometricPrompt.CryptoObject`, and its output binds the RP ID hash, authenticator flags/counter and client-data hash. Registration also requires an operation-bound biometric signature before the new credential is released, even though the supported `none` attestation deliberately omits that proof from the RP response.

Metadata resides in the private application SQLite database with a Keystore-backed HMAC for row integrity. Android backup is disabled. Counter values are durably reserved before signing; rejection/cancellation can leave gaps, never reuse a reserved value. Vault deletion/reset shares the authenticator's operation mutex. Reset closes network operations before requesting separate local fingerprint approval.

The user-visible foreground service listens only on `127.0.0.1:37123`, reached from Windows through ADB forwarding. It accepts mutually authenticated TLS 1.2/1.3 transport identities. Bootstrap trust permits only Hello and explicit pairing; CTAP and status require a pinned paired host. The phone and desktop show the full SHA-256 digest binding both transport certificates: all **64 uppercase hexadecimal characters** must be compared before approval. It is not shortened to a small comparison code. Pairing requires local full-hash confirmation and a real strong biometric; its ceremony still requires independent security review before production use. Revocation removes the pin and closes its sessions. There is one logical computer session at a time; another connection cannot overwrite its dashboard state.

The separate transport identity uses the version-two alias and permits `DIGEST_NONE` plus SHA-256. Android's TLS provider computes the TLS transcript digest itself and uses raw ECDSA to sign that digest; this follows the [Android Keystore TLS digest guidance](https://developer.android.com/reference/android/security/keystore/KeyGenParameterSpec.Builder#setDigests(java.lang.String...)). Credential keys remain SHA-256-only, non-exportable and biometric-bound. A transport-identity change requires desktop identity re-verification/pairing; it does not move or replace credential private keys.

The engine advertises `FIDO_2_0`, ES256, resident credentials and built-in UV only when available. It does not advertise U2F, ClientPIN, CTAP2.1 features, vendor attestation or FIDO certification. Unsupported commands fail explicitly. Unknown forward-compatible options are ignored; malformed known parameters fail. The phone displays the client-supplied RP ID and does not claim to verify the browser origin independently.

Approval cancellation clears its distinct pending result and dismisses any native biometric prompt tied to that request. Cancelled results cannot authorize later operations. Timeout is reported separately from host cancellation. Each CTAP request receives at most one terminal response; failures refreshing local history after publication do not send a contradictory second response.

## Device verification performed on 2026-10-26

Executed against the connected iQOO Neo 10R (`I2221`) reporting Android 16/API 36:

* **27/27 JVM tests passed**: CTAP registration/none attestation, ES256 assertion verification, allow/exclude lists, resident behavior, counters, rejected/cancelled key cleanup, protocol errors, mixed-major canonical CBOR ordering, unknown/invalid options, unsupported descriptor types, framing/zero IDs, pairing identity binding, maintenance serialization, mandatory TLS upgrade, and Windows preflight retaining fresh approval.
* **6/6 device instrumentation tests passed**: non-exportable credential key; per-use biometric requirement and rejection of signing without fresh authentication; key deletion; isolated metadata database persistence/counter reservation/deletion; altered metadata MAC rejection; cancellation clearing a pending approval and preventing result reuse; user-action timeout behavior; a separate non-exportable transport key successfully signing/verifying a TLS transcript digest using `NONEwithECDSA`. Some cases are combined within one test.
* Debug APK built, installed and launched. Ordinary onboarding and Start bridge were exercised. Android reports a foreground connected-device service and a loopback listener on port 37123.
* Real Windows C# client → USB ADB forward → Android mutually authenticated TLS Hello completed successfully and returned the phone's actual identity and matching certificate-derived comparison code.

Device instrumentation deliberately does not automate biometric approval. Separately, the person approved real direct signing and Chrome registration/assertion through the Windows FIDO HID. Windows/WebCrypto verified the returned signatures; the browser accepted counter 2 after its preflight and final assertion. ADB loss removed the virtual HID. Biometric enrollment changes, suspend/resume and physical cable unplugging remain untested. See `verification.md` for the exact evidence.

Windows may first request an assertion with `up=false` to check the selected credential. The phone still requires fresh biometric authorization for that private-key use. Built-in biometric evidence supplies actual UP/UV flags, and a later final assertion requires another fresh approval. This intentionally produces additional phone prompts rather than providing an unattended signature or reusable approval token.

An earlier Gradle `:app:connectedDebugAndroidTest` run executed its then-current five tests successfully but its host-side UTP gRPC reporting channel failed with `UNAVAILABLE: io exception`, causing the Gradle task to report failure. This is recorded rather than counted as a successful Gradle task. The final APK/test APK with six tests were installed without uninstalling application data and rerun directly:

```powershell
adb install -r android/app/build/outputs/apk/debug/app-debug.apk
adb install -r android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell am instrument -w -r dev.keybridge.android.test/androidx.test.runner.AndroidJUnitRunner
```

The final direct runner returned `OK (6 tests)` and `INSTRUMENTATION_CODE: -1`. Gradle's UTP runner can uninstall the application after testing; use a dedicated test installation/device, and do not run destructive build/test workflows against an installation holding important credentials. These instrumentation cases themselves use isolated metadata databases and delete only their newly generated test credential/transport keys.

## Permissions and lifecycle

`INTERNET` permits the loopback TLS socket; `USE_BIOMETRIC` gates approvals; foreground-service/connected-device permissions maintain the visible active bridge; notification permission supports optional approval notifications; vibration supports haptic feedback. `CHANGE_NETWORK_STATE` satisfies Android's connected-device foreground-service prerequisite; KeyBridge does not change network configuration. Notification approval and Android execution restrictions are respected. The service is not exported, does not restart secretly and stops with the user's Stop bridge action.

Keep the phone app open when pairing or approving. Background requests post an invitation to open the app when notifications are allowed; the service does not launch hidden approval activities. If keys are invalidated, delete affected entries and register again using account recovery. App uninstall/reset permanently removes access to those credential private keys.
