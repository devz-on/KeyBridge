# Installation and recovery

## Windows companion

For a complete install, run **KeyBridge-0.1.2-Setup-x64.exe**. It includes the companion/.NET runtime, private ADB tools, offline diagnostic, APK and unchanged verified driver installer. It configures default tool paths and offers interactive elevated driver installation if the inspected version is missing. No browser extension is needed. See [all-in-one setup](installer.md) for payload hashes, installation behavior and limitations.

Build with `scripts/Build.ps1`; create a portable package with `scripts/Package.ps1`. Extract `artifacts/KeyBridge-0.1.2-windows-x64.zip` and run `app/KeyBridge.exe`. The self-contained package includes .NET, so the runtime need not be installed on the target PC. It is a local development build without a commercial code-signing certificate.

For per-user installation, run the extracted package's `Install.ps1`. It copies the companion/tools to `%LOCALAPPDATA%\Programs\KeyBridge` and creates a Start menu shortcut. No administrator service, USB driver, auto-start entry or security change is made. Startup and automatic reconnection are explicit app settings. Exit from the tray before updates/uninstallation.

If your PowerShell policy blocks unsigned scripts, use the portable executable directly or your organization's normal script-signing process. Do not weaken execution policy to install KeyBridge. Builds can also be run with the native `dotnet build KeyBridge.sln -c Release` and `android/gradlew.bat` commands.

`scripts/Doctor.ps1` reports tool and driver presence. Set the actual `adb.exe` and signed `usbip.exe` paths in Settings. Platform Tools must come from the [official Android download](https://developer.android.com/tools/releases/platform-tools), not an unknown repack. An already installed Android SDK supplies `platform-tools/adb.exe`.

## Android phone

The minimum Android API is 30; this build targets API 36. Use a strong screen lock and enroll a strong biometric. Enable Developer options and USB debugging using Android's own Settings. Connect a USB **data** cable. Accept the RSA debugging authorization only for your intended computer. The companion handles unauthorized/offline states and multiple phones without silently choosing one.

Select the phone in the companion's Connection screen and choose Install Android APK, or use:

```powershell
adb -d install -r artifacts/android/KeyBridge-release.apk
adb -d shell am start -n dev.keybridge.android/.MainActivity
```

`-d` deliberately chooses a USB device. With multiple phones use the known serial explicitly. The companion excludes emulator transports; wireless debugging requires its explicit opt-in. On Windows, Platform Tools may return `unknown` for `get-devpath`; the companion then verifies ADB's explicit `-d` USB serial or a present USB PnP serial for multiple devices.

Complete KeyBridge onboarding. Android may request notification permission; decide locally. Start the visible bridge service from the dashboard. A foreground notification keeps the session visible. No root, special accessibility service, device administrator, battery exemption or background activity bypass is needed. `INTERNET` is used for the loopback socket; the listener is not reachable from the LAN. The normal `CHANGE_NETWORK_STATE` permission satisfies Android's connected-device foreground-service prerequisite; the app does not change network settings. No biometric data is stored by KeyBridge.

If Funtouch OS stops the app in the background, reopen it and reconnect. Apply any vendor battery setting yourself only if you want sustained sessions. Authentication approval requires the app's visible UI; the notification brings you back. A locked phone or unavailable biometric fails closed.

## Pair the intended computer

1. Start the phone bridge, select the authorized USB phone and click Connect in Windows.
2. Compare all 64 hexadecimal characters of the fingerprint shown in Windows with the phone's pairing request. Every group must match exactly; a matching prefix is insufficient.
3. Confirm the matching code on Windows, then approve the phone's local pairing prompt. Reject unsolicited or different codes.
4. Both devices pin the opposite transport certificate. No credential operation is approved by pairing alone.

The CLI provides the same explicit process for diagnostics:

```powershell
dotnet run --project windows/KeyBridge.Tool -- hello
dotnet run --project windows/KeyBridge.Tool -- pair --code CODE_YOU_COMPARED
dotnet run --project windows/KeyBridge.Tool -- get-info
dotnet run --project windows/KeyBridge.Tool -- phone-test
```

`phone-test` creates a real diagnostic credential for `keybridge.invalid`, asks for registration and assertion fingerprints, then verifies its ES256 signature and counter on Windows using only the public key. It tests the phone/tunnel path independently of the USB driver. Delete that test credential in the Android vault afterward.

For already-authorized **wireless debugging**, add `--wireless` to CLI commands or enable **Allow already-authorized wireless ADB devices** in Windows Settings. `KeyBridge.Tool enable-wireless` saves this preference for the next companion launch. KeyBridge does not enable network debugging, connect to an unknown phone or approve Android's debugging-pairing dialog. Both authenticator listeners remain loopback-only behind ADB forwarding, and application-level full-fingerprint pairing/pinning/biometric requirements remain mandatory. The final physical signing test was performed on this explicitly authorized wireless path. USB-only acceptance remains a separate test when a cable is used.

Windows currently uses TLS 1.2 ECDHE-ECDSA/AES-GCM for this channel. TLS 1.3 client-certificate interoperability failed on this host and is not a supported Windows mode in this release. This changes no Windows security settings.

`scripts/Test.ps1 -Device` uses the standard Gradle connected-device runner. If its host UTP gRPC reporting channel fails, the separate `-DirectDevice` option installs the same test APK and invokes AndroidJUnitRunner directly over USB, checking the actual instrumentation result. The failed Gradle task remains a recorded harness failure; it is not relabeled as passed. Device instrumentation restarts the app, so reopen it and start the bridge afterward.

## Virtual USB driver

Read [driver verification](driver-verification.md). A compatible, properly signed usbip-win2 client VHCI driver is essential for Windows WebAuthn. The all-in-one setup offers its original privileged installer with explicit approval; the companion does not install drivers silently or change security settings. After pairing, use Connection and click **Enable security key**. That action starts the backend and attaches the key, then checks Windows PnP/HID. Browser acceptance remains a separate test.

Use the local test page in `tests/webauthn` or [webauthn.io](https://webauthn.io/) with throwaway credentials. Choose the external security-key flow in Windows. Verify that the request reaches the physical phone and requires fingerprint approval. Do not use a valuable account for development testing.

## Releases and signing

Published Android APKs use the release variant with debugging disabled and a dedicated signing key. Build an unsigned release using `android/gradlew.bat :app:assembleRelease`, then use `scripts/Sign-AndroidRelease.ps1` with a protected signing identity. The official APK includes a predecessor signing lineage for the original maintainer development build. See [release signing](release-signing.md) for the certificate fingerprint, verification commands, migration limits and maintainer procedure. Keep private keys and passwords outside Git.

Windows is published with `dotnet publish`, not a kernel driver build. Sign published executables/MSIX or an installer with your own trusted release certificate in your release system. Do not present unsigned development binaries as publisher verified. The portable install/uninstall scripts are source-reviewable and do not replace commercial installer certification.

## Safe uninstall and deliberate cleanup

Stop the bridge, exit the tray application, and revoke the computer in Android's Paired computers screen. Run the extracted `Uninstall.ps1`; `-RemovePreferences` additionally removes `%LOCALAPPDATA%\KeyBridge` after confirmation. It leaves the Android vault and unrelated external USB/IP driver untouched. Remove the latter using its upstream uninstall instructions if you deliberately choose to do so; USB devices may restart.

The desktop's transport certificate is in **Current User / Personal** with friendly name `KeyBridge local transport identity v1`. Its CNG key is named `KeyBridge.Transport.<random>`. For deliberate identity removal, use the current-user certificate manager and remove only this identity; CNG key lifecycle cleanup should be handled by an administrator/developer after verifying its exact name. No credential private key is present on Windows. Revocation on the phone is the authorization removal that matters.

Uninstalling Android deletes the app's Keystore keys. Credential reset/deletion also destroys private keys and cannot be undone. Remove those passkeys from the corresponding services using an alternate authentication method first. KeyBridge has no recovery backup or private-key export.
