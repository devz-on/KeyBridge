# Windows setup package

The current release is `KeyBridge-0.1.2-Setup-x64.exe`, built from `packaging/KeyBridge.iss`. It targets Windows 11 x64 and installs the companion for the current user. Run setup normally; only the separate driver step needs administrator approval.

## Installation order

1. Setup copies the self-contained companion, CLI, offline WebAuthn diagnostic, private ADB tools, Android APK, notices and documentation. It creates a Start menu entry and, by default, a desktop shortcut.
2. The completed page offers **Install the signed USB/IP driver** if the inspected driver/client is missing. After Finish, that step opens the original elevated upstream installer. The KeyBridge wizard is hidden while it runs.
3. The companion opens after the selected driver step closes. **Checks** reports what is available and what remains. If the driver installation is canceled or fails, the companion stays installed; setup can be run again to retry.

The upstream command uses `/NORESTART /RESTARTEXITCODE=3010 /TYPE=compact /TASKS="vcredist"`. Its compact type supplies driver/client components and retains its own Visual C++ runtime task and licenses. USB devices may briefly reconnect. KeyBridge does not restart Windows automatically; handle a requested restart manually. Silent setup skips the interactive driver step.

## Included files

- Self-contained Windows companion, CLI and diagnostic server, including the .NET runtime.
- ADB 37.0.1: `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`, `libwinpthread-1.dll`, `NOTICE.txt` and `source.properties`.
- The matching signed Android release APK, with debugging disabled. Choose the intended phone in Connection to install it; setup never chooses an arbitrary phone.
- The unchanged official signed usbip-win2 0.9.8.1 installer.
- Project/dependency licenses and setup, architecture and verification documentation.

No browser extension, Android Studio, Java, full Android SDK, fastboot, generic unsigned phone driver or system PATH edit is needed. Phone-specific USB interface drivers remain the manufacturer's responsibility if Windows cannot detect a device. Authorized wireless ADB is an explicit alternative.

## Verification and trust

The build rejects unexpected prerequisite hashes and driver/compiler signatures. Immediately before the driver opens, setup extracts its embedded payload and verifies its SHA-256 again. Runtime attachment separately checks the installed client's Authenticode publisher, driver health and Windows HID detection. Attachment is enabled by default; it never approves a phone signing operation.

| Official prerequisite | SHA-256 |
| --- | --- |
| Platform Tools 37.0.1 Windows ZIP | `45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99` |
| usbip-win2 0.9.8.1 x64 installer | `38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A` |
| Inno Setup 6.7.3 compiler distribution | `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732` |

The Platform Tools ZIP also matched Google's SDK repository SHA-1 metadata. The driver installer had a Valid Cloudyne signature; the compiler had a Valid Pyrsys B.V. signature matching its release digest. Kernel signatures and catalog membership checks are recorded in [driver-component-verification.md](driver-component-verification.md).

The KeyBridge setup is currently an **unsigned development executable**. Its embedded upstream driver retains its original signatures. A production release needs protected maintainer signing, wider installation testing and independent verification. Do not bypass a Windows security warning or weaken platform protections to install it.

The selected ADB files keep the complete Platform Tools notices. Google's [SDK terms](https://developer.android.com/tools/releases/platform-tools), section 3.5, distinguish open-source components from the rest of the SDK. The driver release and Inno notices are included under `licenses`; see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Build

Install official [Inno Setup 6.7.3](https://jrsoftware.org/isdl.php), configure the protected Android [release signing identity](release-signing.md), then run:

```powershell
.\scripts\Build-Installer.ps1
# Or reuse current Package.ps1 outputs:
.\scripts\Build-Installer.ps1 -SkipPackage -IsccPath 'C:\path\to\ISCC.exe'
```

Verified prerequisites are cached under ignored `.cache/installer`. Building does not install the driver on the build PC. Output is `artifacts/releases/0.1.2/`; `-OutputDirectory` selects another location. The portable ZIP is produced separately by `Package.ps1`.

## Update and uninstall

Exit the companion before updating. Existing valid tool paths and pairing data are preserved; missing paths fall back to the private ADB folder and standard USB/IP client installation. Keep the Android signing identity and use `adb install -r`; uninstall/reset destroys credential keys.

Use Windows Apps → KeyBridge → Uninstall. It removes its own files, shortcuts and startup value. It preserves local preferences, the transport identity, Android vault, shared ADB server and separate USB/IP driver. Remove that driver through its own uninstall entry only when no other software needs it.

See [verification.md](verification.md) for executed checks. Building and inspecting the package do not prove a successful privileged driver install on a clean PC. Fresh-machine installation and broader upgrade/uninstall acceptance remain outstanding.
