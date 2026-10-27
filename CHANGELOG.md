# Changelog

## Release packaging for 0.1.2

- Fixed the README sequence diagram's Mermaid parsing error.
- Published Android builds use a dedicated release signing key and disable debugging. The APK includes a signing lineage for migration from the original maintainer development identity (physical migration verification pending).
- Windows setup bundles the same signed release APK as the standalone download. Packaging rejects debug signing and debuggable APKs.

## 0.1.2

- Standardized page spacing, headings, buttons and shorter interface copy across Windows and Android. Windows app, taskbar, tray and setup use the Android shield/key icon.
- Restored Windows Checks with reported ADB, signed driver/client, phone connection, pairing, biometric readiness, backend and HID detection. Unknown readiness remains incomplete.
- Enabled signed-driver attachment by default; runtime verification and phone approval remain enforced.
- Moved the driver's own elevated wizard to the post-install Finish step, with the KeyBridge wizard hidden while it runs. Desktop shortcuts are selected by default.
- Added public source/contribution/security documentation, issue and pull-request templates, GitHub Sponsors configuration and source/sponsor links in both About screens.

## 0.1.1 — 2026-10-26

- Windows: consolidated Home/Setup/Devices/Security key into Connection; three primary destinations remain. Diagnostics and About are reached from Settings, with a return button. One Enable security key action starts and attaches the backend after explicit attachment consent.
- Android: replaced the More menu with Settings; removed the duplicate Connection screen, keeping bridge control on Home. System Back and the toolbar arrow retrace actual tab and detail visits. History and selected credential survive activity recreation; reselecting a tab does not add a duplicate history entry.
- Added a single Windows setup executable with self-contained .NET apps, ADB/libraries/notices, offline diagnostic, phone APK, and the original verified signed USB/IP driver installer. Tool paths are detected automatically. Driver installation remains interactive/elevated; no browser extension, silent phone approval, security weakening or automatic reboot is introduced.

## 0.1.0 — 2026-10-26

Initial implementation of Android Keystore authenticator, biometric approval, credential vault and lifecycle, paired mTLS USB ADB transport, Windows companion, USB/IP and CTAPHID backend, protocol/device tests, build and portable packaging scripts, and setup/security documentation.

Verified on Windows 11 build 26300 with the physical iQOO: pinned pairing, biometric Keystore signing, signed USB/IP driver loading, FIDO HID enumeration, Chrome registration/assertion verification and virtual-key removal on ADB loss. The phone link used explicitly authorized wireless debugging. See `docs/verification.md` for evidence, exact scope and remaining acceptance gates.
