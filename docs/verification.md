# Verification and acceptance record

Updated: **2026-10-27**, Asia/Kolkata. This records executed observations separately from source implementation. No FIDO certification or independent security audit has occurred.

## 0.1.2 signed Android packaging

- The release variant (version code 4) compiled successfully, including release lint. Android JVM tests passed. Debugging is explicitly disabled and the built manifest has no `application-debuggable` flag.
- Android SDK `apksigner` verified the dedicated KeyBridge release certificate and v3 APK signatures for API 30, 31, 33 and 36. The embedded signing lineage matches the original development certificate and the published release certificate. Installed-data migration is enabled and predecessor rollback is disabled. The signing script rejected a debuggable APK before invoking signing.
- The physical phone was unavailable for the release-key migration. The earlier version-code-3 UI and readiness results below remain valid for their tested build; they do not establish that the signed version-code-4 APK has updated the phone. No uninstall, data reset or credential operation was attempted for this packaging change.

## 0.1.2 interface and release verification

- Windows Release builds and self-contained publishing succeeded. The companion now has Connection, Checks, Activity and Settings, a shared Android/Windows icon, shorter labels and consistent action spacing. Signed-driver attachment defaults to enabled while runtime client/driver verification remains enforced.
- The running Windows UI was inspected at its standard window size. Checks first showed 5/10 with only the authorized USB device available. After starting the real phone bridge, reconnecting its existing pinned identity and enabling the virtual key, all ten checks completed: **Ready to use**, actual strong-biometric availability, reported TEE storage, and **FIDO HID detected**. No credential-signing approval was automated or requested for this readiness test.
- Android 0.1.2/version code 3 updated over USB without uninstalling. Home and Settings layouts were inspected on the physical phone. Home → Vault → Activity → Settings → Computers returned via system Back through Settings → Activity → Vault → Home. Existing vault entries remained visible.
- Current local software checks passed: **24 USB/HID, 16 transport/readiness, 30 Android JVM and 19 browser-helper tests**, plus the loopback HTTP server checks. The readiness regression distinguishes unknown, unavailable and available biometrics without invoking a credential key.
- The compiled setup was extracted for inspection. Critical companion/core, APK, ADB/libraries/notices and original driver payload hashes matched the build inputs. The embedded driver's expected upstream signature remained Valid. Recovered setup entries confirm default desktop shortcuts and a post-install, elevated driver step with the companion wizard hidden and automatic restart disabled.
- These checks do not establish a fresh-PC privileged driver installation, all Windows DPI/accessibility configurations, or new browser signature acceptance for 0.1.2. The earlier real browser result below is retained with its original scope.
## 0.1.1 navigation and setup follow-up

- Windows now has Connection, Activity and Settings. Connection combines device selection, pairing and virtual-key activation. Diagnostics/About are Settings details. Release and self-contained publishing compiled successfully; the desktop visual review for that revision was incomplete. The 0.1.2 review is recorded above.
- Android now has Home, Vault, Activity and Settings. The duplicate Connection/More screens are removed. `ScreenHistoryTest` adds **3 passing tests** for chronological Back across revisited tabs, detail parents, duplicate selection and bounded history; the JVM suite is now **30/30** (the earlier table below records 27/27 before this addition).
- The final 0.1.1 APK (**version code 2**) updated successfully over USB with `adb install -r`. On the actual phone, Home → Vault → Activity → Settings → Paired computers returned via system Back to Settings → Activity → Vault → Home. Re-selecting Vault added no Back step. Phone vault controls were not deleted/reset, and security instrumentation was not rerun. A Settings screenshot is saved under `artifacts/screenshots/android-settings-0.1.1.png`.
- The all-in-one **KeyBridge-0.1.1-Setup-x64.exe** compiled using verified official Inno Setup 6.7.3. It embeds self-contained companion/tool/diagnostic applications, selected official ADB 37.0.1 files and notices, the APK and the unchanged verified signed usbip-win2 driver installer. It offers an interactive elevated driver step when needed, with hash verification and no automatic restart. No extension is required. See [installer.md](installer.md).
- The earlier browser and driver runtime results below apply to the tested 0.1.0 flow. The 0.1.1 authenticator protocol/security implementation is unchanged apart from navigation support in the pure core. A full new-installer fresh-machine privileged install, new-layout Windows visual pass and broad upgrade/uninstall acceptance remain pending; compilation alone does not establish those results.

## Environment

- Windows 11 Pro x64, build **26300**.
- .NET SDK **10.0.403**, installed per user from the official Microsoft distribution; builds are reproducible through `KeyBridge.sln` and scripts.
- JDK **21.0.12.1**, Android SDK platform/build-tools **36**, Gradle **8.13**, Kotlin **2.2.10**, Android Gradle Plugin **8.13.2**.
- Platform Tools **37.0.1**, authorized physical USB phone reporting **vivo I2221 / Android 16 API 36**, identified by the user as iQOO Neo 10R. The device serial is deliberately omitted here.
- Android Keystore diagnostic reports **Trusted execution environment**, with **strong biometric available**. This is a measured result, not a StrongBox assumption.
- After explicit user consent, the verified usbip-win2 **0.9.8.1** installer was run with `/NORESTART`. Both `usbip2_ude` and `usbip2_filter` report **Running**, and Windows reports signed controller driver version **12.1.30.403**. No reboot occurred. Secure Boot/signature enforcement/test-signing/Norton settings were not changed.

## Executed automated verification

| Suite | Result | Scope |
| --- | --- | --- |
| Windows solution Release | **Passed**, zero warnings/errors | Native app, bridge, USB/IP, tools and test projects compile |
| `KeyBridge.Usb.Tests` | **24/24 passed** | CTAPHID, descriptors, bounds, transactions, real loopback USB/IP, UNLINK, queue pressure and shutdown |
| `KeyBridge.Core.Tests` | **15/15 passed** | Fragmented frames, malformed/oversize/replayed data, mTLS, pin changes/ADB aliases, wireless opt-in, pairing gate, cancellation, disconnect, mandatory TLS upgrade and 100 concurrent request IDs in order |
| Android `:core:test` | **27/27 passed** | Canonical CTAP CBOR, frames, CTAP registration/assertion, ES256 verification, counters, allow/exclude/discovery, Windows preflight with mandatory approval, cancellation/maintenance and mandatory TLS upgrade |
| Direct Android instrumentation | **6/6 passed** | Real Keystore policies/non-exportability, refusal without authentication, metadata persistence/counters/deletion/tamper checks, approval cancellation/timeout and raw TLS ECDSA authorization |
| Browser helper tests | **19/19 passed** | Bounded CBOR/COSE, one-use challenges, RP/origin/UV checks, real WebCrypto ES256 verification with independent test signature and rejection paths |
| Offline diagnostic server | **Passed** | Build, loopback binding, route whitelist, CSP, Host/Origin/method/header rejection and recovery |
| PowerShell build scripts | **Parsed; Build.ps1 executed successfully** | Full Windows/Android build and JVM tests |
| Self-contained Windows publishing | **Passed** | Actual x64 companion, tool and offline diagnostic server package generated |

Important runner issue: the expanded Gradle `connectedDebugAndroidTest` executed five passing device cases but its **host UTP gRPC channel failed**, so that Gradle task is **failed (harness)**. Direct `adb shell am instrument` against those APKs returned `OK (5 tests)` / `INSTRUMENTATION_CODE: -1`; a later direct run of the final expanded device suite passed **6/6**. This does not relabel the failed Gradle task as passed. The original three-test Gradle device run passed before the reporting problem. See `docs/android.md` and `Test.ps1 -DirectDevice` for the explicit alternate runner.

Early integration testing found and fixed a separate TLS-identity key authorization issue (TLS needs Keystore `DIGEST_NONE` for its internal transcript signer). Credential keys still authorize only their own biometric-gated SHA256/ECDSA signing operations. The real Windows-to-Android mutual-TLS Hello now succeeds over the authorized USB ADB forward. Failed attempts before that correction were not considered successful pairing.

The final real-device test used **wireless debugging at the user's explicit request**. USB remains the default; the companion adds an explicit wireless option and never opens an Android LAN authenticator listener. The tested Windows configuration uses TLS 1.2 with **ECDHE-ECDSA/AES-GCM**; TLS 1.3 interoperability is not claimed. Certificate pin mismatches remain fatal; no pin is silently replaced. Diagnostics proved that Norton Web/Mail Shield was substituting its own certificate and interfering with client authentication. The application now identifies its binary service with a mandatory fixed protocol upgrade and `keybridge/1` ALPN before any authenticated application command; Norton and Windows protections were not changed. Pinned TLS reconnects then passed reliably.

**Physical signing test passed:** the person explicitly authorized desktop pairing, compared the complete SHA-256 fingerprint and approved locally. `KeyBridge.Tool phone-test --wireless` created a real `keybridge.invalid` discoverable credential, obtained separate registration and assertion fingerprints, then verified the ES256 assertion on Windows against the returned COSE public key. RP hash, UP/UV/AT registration flags, none attestation, UP/UV assertion flags and signature counter **1** were checked. No credential private key left the phone. The diagnostic credential remains in the phone vault for deliberate user deletion.

**Full browser path passed:** Chrome on `http://localhost:8787` created a credential using the real Windows WebAuthn/security-key flow and the actual virtual HID. The person approved on the physical phone. After correcting Windows `up=false` preflight handling while retaining fresh fingerprint approval, WebCrypto verified the actual ES256 assertion, challenge, exact origin, RP hash, credential/user identity, UP/UV and persistent counter **2**. Two key uses (preflight and final assertion) explain the counter. The browser reported **“The assertion is cryptographically valid.”** Evidence image: `artifacts/screenshots/webauthn-verified.png`. No browser virtual authenticator or software phone substitute was used.

**Connection-loss check passed:** targeting this wireless device with ADB reconnect closed the active tunnel. The running controller reported connection loss and Windows' present-device query returned **zero KeyBridge HID devices**. ADB was restored to the already-authorized endpoint afterward, and pinned GetInfo succeeded using the same identity under its new ADB routing alias. The temporary test bridge and static diagnostic server were then stopped. This checks actual ADB loss/detach without claiming a physical cable-unplug test or an in-flight biometric interruption test.

Synthetic credential keys/public responses appear only in test fixtures. Production Windows code cannot sign a credential or fall back to a Windows FIDO private key. A passing software signature test alone does not prove a phone biometric operation.

## Hardware and application observations

| Acceptance criterion | Status | Evidence / remaining work |
| --- | --- | --- |
| Unrooted Android app runs | **Verified** | Debug APK installed/launched on physical I2221; native onboarding/dashboard inspected; process/foreground service observed |
| Windows discovers phone over USB | **Verified** | Real authorized ADB device; explicit USB selector and PnP identity fallback checked |
| Real authenticated USB tunnel | **Verified** | Windows CNG transport identity ↔ Android Keystore TLS identity; successful actual Hello and certificate-bound comparison code |
| Secure user-confirmed pairing | **Verified** | Complete 64-hex SHA-256 comparison, explicit desktop consent and local phone approval; pinned reconnect succeeded |
| Non-exportable Android key creation | **Verified** | Actual provider policy instrumentation plus user-approved Keystore credential creation in direct and browser flows |
| Requests reach phone approval UI | **Verified over wireless ADB** | Real registration and assertion reached the physical phone and were approved by the person |
| Fingerprint protects real signing | **Verified for tested flow** | Unauthorized signing rejected by device tests; separate user-approved registration/assertion completed |
| Valid signed responses return to Windows | **Verified over wireless ADB** | Windows verified actual Android ES256 assertion, RP hash, UV/UP and counter 1 |
| Virtual FIDO HID enumerates in Windows | **Verified** | Actual present healthy `HID-compliant fido`, VID/PID 1209:0001 and FIDO usage F1D0:0001; signed driver loaded without a reboot |
| Browser WebAuthn registration/assertion | **Verified for Chrome/localhost** | Actual registration and assertion; ES256, challenge, origin, RP hash, identities, UP/UV and counter 2 passed. Other relying parties and browsers remain untested |
| ADB loss removes virtual key | **Verified** | Real wireless ADB disconnect closed transport; present KeyBridge HID count became zero; pinned reconnection succeeded |
| Physical unplug gracefully disables auth | **Not yet tested** | Software disconnect/cancel/USB-IP shutdown tests pass; physical operation-in-flight test remains |
| UI navigation and controls | **Partly verified** | All eight native Windows pages inspected; light/dark/reduced motion; Android onboarding/dashboard/security observed. Full paired-device controls, accessibility/DPI and all error/recovery flows require more testing |
| Automated tests | **Verified within scope** | Counts above; Gradle UTP reporting failure explicitly retained |
| Documented installations/build artifacts | **Verified within scope** | Built APK and Windows binaries/packages; user-approved signed driver installation/loading tested. Android release signature verified locally; physical release-key migration and full uninstall remain untested |

## Deliberate capability limits

The implemented profile is biometric CTAP2.0/ES256 with `none` attestation and local account choice. U2F, ClientPIN, CTAP2.1 extensions/credential-management commands, vendor attestation and FIDO certification are unsupported and not advertised. Reset/deletion is handled by explicit local Android controls. The Windows process is per-user with a visible optional tray, not an installed system service. No kernel driver, Platform Tools downloader, cloud account, recovery export or production code-signing key is bundled.

Biometric enrollment-change recovery, app/OS crashes during signing, hardware unplug/suspend/restart, multiple physical phones together, long-running battery behavior, independent accessibility/DPI testing, official FIDO conformance vectors/tools, third-party security review and complete relying-party interoperability remain required before a production release. The development VID/PID needs a proper allocation. Premium interface polish and observed navigation do not replace these acceptance gates.
