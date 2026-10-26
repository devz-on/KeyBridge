# External USB/IP driver verification

KeyBridge implements the USB/IP **server** in user space. Windows additionally needs the **client VHCI/UDE driver** from [vadimgrn/usbip-win2](https://github.com/vadimgrn/usbip-win2). The upstream requirements include Windows x64 version 1903 or newer and USB/IP protocol 1.1.1. This requirement is not proof of compatibility with every later Windows build. See [upstream releases](https://github.com/vadimgrn/usbip-win2/releases) and known issues before choosing a package.

## Observed evidence on 2026-10-26

Host: Windows 11 Pro x64, build **26300**. No `usbip2_ude` VHCI service or device was installed at the initial check. The initial inspection below was performed before installation consent.

The following upstream installer was downloaded to a temporary directory for **read-only** inspection. It was not run, redistributed or bundled in the KeyBridge package:

| Field | Observed value |
| --- | --- |
| Release | `v.0.9.8.1` |
| Asset | `USBip-0.9.8.1-x64.exe` |
| Source | [official release asset](https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe) |
| SHA-256 | `38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A` |
| GitHub release API digest | Same SHA-256 |
| Authenticode | `Valid` / “Signature verified.” from Windows |
| Publisher | Cloudyne Systems (Scheibling Consulting AB), Sweden |
| Signer certificate thumbprint | `9AC56B6C76141395D74FFF6652818376E80B9C95` |

The initial container check alone did not verify kernel catalogs or runtime behavior. Subsequently, [component verification](driver-component-verification.md) validated the extracted drivers/catalogs with 10 successful kernel-policy, catalog-membership and target-OS checks. After explicit consent, this exact installer was run with `/NORESTART`; both installed drivers report Running with valid Microsoft signatures, Windows enumerated the FIDO HID, and Chrome registration/assertion passed. See [verification](verification.md). The linked upstream certification report was not independently retrieved, and no FIDO certification is claimed.

## Consent-based installation

First review upstream release notes and known issues. Save work involving USB devices and understand the recovery/uninstall procedure. The upstream installer may restart USB devices and request a reboot. Do not use a test-signed package, disable Secure Boot, or enable test signing for KeyBridge.

`scripts/Install-VerifiedDriver.ps1` accepts only the inspected x64 installer hash and signer above. It refuses to launch without `-Consent`; `-WhatIf` previews the final action. The final action opens the upstream interactive administrator installer with `/NORESTART`. It does not silently install, reboot, accept installer prompts or change protection settings. A new target computer still needs its own runtime compatibility checks.

```powershell
.\scripts\Install-VerifiedDriver.ps1 -Installer C:\Downloads\USBip-0.9.8.1-x64.exe -Consent -WhatIf
# Review the concrete result, then run without -WhatIf only if you choose to install.
```

After installation, check Windows device/driver health and catalog signatures using trusted Windows tooling. The companion reads Windows' signed-driver metadata and validates `usbip.exe` Authenticode against verified upstream publisher identities before invocation. `Windows reports signed` is deliberately distinguished from independently verified driver compatibility.

Set the signed `usbip.exe` path in Settings. Pair the phone, start USB/IP, give explicit session consent, then Attach. The client command is `usbip.exe attach -r 127.0.0.1 -b 1-1`. A present, healthy HID device with development VID/PID **1209:0001** and FIDO usage **F1D0:0001** is checked separately from the attach command's exit code. The VID/PID is for development only; obtain an allocation before release distribution.

If any verification fails, keep the driver detached and continue using `KeyBridge.Tool phone-test` for the real Android signing path. That diagnostic is not equivalent to Windows WebAuthn integration.
