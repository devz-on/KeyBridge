# Driver component verification — usbip-win2 0.9.8.1 x64

Verification date: **2026-10-26**. Target computer: **Windows 11 Pro 26H2, x64, build 26300.9711**.

**Inspection result:** The exact upstream installer, extracted kernel drivers, driver catalogs, and catalog membership checks passed the integrity/signature checks below. The AMD64 package declares an OS range covering this Windows build. At the time of this inspection, installation and runtime interoperability had not been tested. Signature verification alone is not proof of runtime compatibility or absence of driver defects.

During the inspection, the installer was **not executed**, including in extraction mode. No driver/service was installed, no catalog was registered, and no boot or security setting was changed. Extraction and verification files are under `%TEMP%\keybridge-driver-component-inspection`.

**Subsequent user-approved runtime verification, same date:** The exact verified installer was installed with `/NORESTART`. Both `usbip2_ude` and `usbip2_filter` loaded, and the installed controller reported signed driver version **12.1.30.403**. Windows enumerated a healthy KeyBridge **HID-compliant fido** device. Actual Chrome/localhost registration and authentication through the physical Android phone passed, including cryptographic assertion verification and counter **2**. Losing ADB removed the virtual HID. The temporary bridge was then stopped. No reboot or protection-setting change occurred. See [verification.md](verification.md) for the complete scope and remaining compatibility tests; this does not establish general HVCI or browser compatibility.

## Exact artifact and provenance

Official release: [usbip-win2 v.0.9.8.1](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1), published 2026-09-27. Download: [USBip-0.9.8.1-x64.exe](https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe).

* Installer size: **26,083,632 bytes**.
* SHA-256: `38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A`.
* Hash matches the SHA-256 digest returned by the official GitHub release-assets API.
* Windows Authenticode result: **Valid**.
* Publisher: **Cloudyne Systems (Scheibling Consulting AB)**.
* Publisher certificate thumbprint: `9AC56B6C76141395D74FFF6652818376E80B9C95`.
* Installer timestamp authority: **Microsoft Public RSA Time Stamping Authority**.

The container uses Inno Setup data format `7.0.0.3`. The externally run reader was Jürgen Rathlev's **innounp 2.70.1**, downloaded from the author's repository at the immutable URL pinned in [Microsoft's Winget manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/j/JurgenRathlev/innounp/2.70.1/JurgenRathlev.innounp.installer.yaml). Its ZIP SHA-256 was checked before use:

`D5514CAC741F12CA94F642EACD30D514D820DCE016E4D016325413DF472C34EA`

This reader parsed the installer as data; neither the installer nor any executable/driver extracted from that installer was run. Selected extraction included the two SYS/INF/CAT groups, CLI executable and packaged README.

## Extracted components

| Component | Bytes | SHA-256 |
| --- | ---: | --- |
| `usbip2_ude.sys` | 157,200 | `ABD4FC43DCE40E027E4ACC884CBFE92433E026206F6565C5EE5284A39441169A` |
| `usbip2_ude.inf` | 2,964 | `1AE9E8DC497929DE1A6BE1291AE1DCA6A19E7FD6C0351E8C266F65DD91F15073` |
| `usbip2_ude.cat` | 11,404 | `84DD6CC5985857ADF1514A9EE202210FF3EE1384C9BE40FD49D6810E23E25F3A` |
| `usbip2_filter.sys` | 61,928 | `B93A5FC3C23D56F9B49634742374E32F269E53F764CAA2D5FCB43E3259F7ECED` |
| `usbip2_filter.inf` | 2,051 | `B7FE01B04793D2F83CF3407601F2C835F1A10C2618E32A52C8FDEF1DA6B79BFF` |
| `usbip2_filter.cat` | 11,425 | `F259D60BDB5AD9D7A976418BFC9156B788DFCEC9B67C8D05D036EB03FB18FFE1` |
| `usbip.exe` | 1,399,544 | `817DA219FA153B4B37C3BC1AA8FCC63F33E046188C57CC3DD2C60A6EF0883C56` |

Both SYS files and both CAT files have **Valid** embedded Authenticode signatures from **Microsoft Windows Hardware Compatibility Publisher**, certificate thumbprint `32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21`, chaining to Microsoft Root Certificate Authority 2010 through Microsoft Windows Third Party Component CA 2012. SignTool also validated their timestamp chain. INF files do not have individual embedded signatures; their membership in the signed catalogs was verified separately. The extracted CLI's signature is Valid and matches the Cloudyne publisher above. PE headers identify both drivers and the CLI as AMD64 (`0x8664`).

Both INFs contain `DriverVer = 09/22/2026,12.1.30.403`. UDE identifies `ROOT\USBIP_WIN2\UDE` and has the AMD64 Windows 10 build-17763-or-later model decoration. The upper filter targets `USB\ROOT_HUB30` and requires build 18362 or later. The catalogs' OS attribute is `2:10.0`.

## Executed Windows verification

Used Microsoft's **Windows SDK BuildTools 10.0.28000.2705** NuGet package, retrieved directly from the official NuGet service. The package's SHA-512 matched its NuGet catalog metadata, and `dotnet nuget verify --all` completed successfully for its Microsoft author and NuGet repository signatures. Package SHA-256:

`8BFDFB6CA2633F531CF80B5FA22512BA61A394D7988F0970DB83BAADC67929ED`

Only its local verification tools were used. `signtool.exe` and its required local runtime DLLs had Valid Microsoft Corporation Authenticode signatures. SignTool SHA-256:

`D6C04707C59A67FCE9FA4BB5FE76C79445B60E1E326D8B500A2F2BA8707C590A`

The following **10 checks all returned exit code 0, zero warnings and zero errors**:

| Check | UDE | Upper filter |
| --- | --- | --- |
| Kernel policy + explicit catalog membership, SYS | Passed | Passed |
| Kernel policy + explicit catalog membership, INF | Passed | Passed |
| Windows-driver policy + catalog membership for OS `2:10.0.26300`, SYS | Passed | Passed |
| Windows-driver policy + catalog membership for OS `2:10.0.26300`, INF | Passed | Passed |
| Kernel-policy validation of CAT itself | Passed | Passed |

Representative commands, using local extracted paths:

```powershell
signtool verify /kp /v /c usbip2_ude.cat usbip2_ude.sys
signtool verify /kp /v /c usbip2_ude.cat usbip2_ude.inf
signtool verify /v /o 2:10.0.26300 /c usbip2_ude.cat usbip2_ude.sys
signtool verify /v /o 2:10.0.26300 /c usbip2_ude.cat usbip2_ude.inf
signtool verify /kp /v usbip2_ude.cat
```

The same five checks were run for `usbip2_filter`. This SignTool rejects combining `/kp` and `/o`, so kernel-policy and OS-target checks were performed separately rather than claiming that a rejected combined command succeeded. Results are saved in the temporary inspection directory as `verification-summary.json` and per-component verification logs.

## Compatibility and installation review

[Upstream requirements](https://github.com/vadimgrn/usbip-win2/tree/v.0.9.8.1#requirements) state Windows x64 build 18362 or later and USB/IP 1.1.1. This machine meets those declared prerequisites; the extracted INF decorations and OS-target signature checks agree. No explicit upstream test report naming **26300.9711** was found. The release links a Microsoft Hardware Certification Report, but that portal report could not be retrieved during this inspection, so no build-specific certification assertion is made.

[Release notes](https://github.com/vadimgrn/usbip-win2/wiki/v.0.9.8.1) describe kernel race/bounds fixes and substantial changes. The release itself flags possible regressions and notes a serial-number-override issue corrected after the release. KeyBridge's attach command does not request a serial override. Older packages were not substituted: previous release notes include a known memory-corruption/BSOD problem in 0.9.7.8.

The packaged README warns that installation restarts USB 3.0 hubs, briefly interrupting connected USB devices. Static upstream installer source also describes driver/service installation and a scheduled detach task on shutdown/reboot, with an optional Visual C++ runtime installation. These are system changes requiring explicit user consent; none occurred during the inspection. The subsequent installation is recorded above. Upstream developer/test-signing instructions are not part of KeyBridge's installation path: the inspected drivers have Microsoft signatures, and Secure Boot, memory integrity and driver-signature enforcement must remain unchanged.

Installation acceptance requires the installed service/driver versions and signatures, successful loading under existing protections, KeyBridge's actual FIDO HID usage/interface enumeration, browser registration/assertion, and detach/recovery. Those checks subsequently passed within the scope recorded above. Other Windows builds, hardware configurations, relying parties and long-duration driver behavior remain untested.

## Developer-root trust and protection-setting inspection

Additional static inspection was performed against the **same exact installer hash above**, without starting it. `innounp -m` recovered `install_script.iss` and the 17,630-byte `embedded/CompiledCode.bin` under the temporary inspection directory's `installer-script` folder.

The released installer's recovered tables contain:

* No `.pfx`, `.cer` or `.crt` payload, no `[Registry]` section, and no certificate-store import action.
* `[Run]` actions for the packaged README, optional Visual C++ runtime, `pnputil` filter installation and `devnode` UDE installation only. There is **no `certutil` invocation**.
* An `[UninstallRun]` action removing the UDE device; there is no developer-certificate import/removal action in that table.
* Optional tasks for the Visual C++ runtime and desktop shortcut. Neither has an `unchecked` flag in the recovered table, so both default to selected when applicable. The default first installation type is full; it includes the GUI, SDK and PDB components as well as the main/client files. Main/client are fixed in upstream source.

The compiled Pascal script's imported names and ASCII/UTF-16 constants were also inspected. They include `NtQuerySystemInformation` and `IsTestSigningModeEnabled` **read/check** functions, plus registry **read** helpers, driver-removal commands, previous-version uninstall handling and Task Scheduler COM operations. No `bcdedit`, certificate-import command, `RegWrite*` helper, Secure Boot control, memory-integrity/HVCI setting, `nointegritychecks`, `testsigning on`, `powershell` or `reg.exe` command was present. The normal package's script registers its shutdown/reboot detach task; it does not expose a security-protection toggle. This is static inspection of recovered tables/imports/constants, not an assertion that every compiled instruction received a formal audit.

There is an important distinction from rebuilding upstream developer sources. [Tagged `setup.iss`](https://github.com/vadimgrn/usbip-win2/blob/55e1fa7/userspace/innosetup/setup.iss) defines `TEST_SIGNED_DRIVERS` by default and derives `INSTALL_TEST_CERTIFICATE` from whether that symbol is defined. In that developer branch, the script packages `usbip.pfx` and imports it into the root store using `certutil`; the branch also checks whether test signing is already enabled and displays instructions when it is not. It does **not** execute `bcdedit` automatically. These developer settings are not controlled merely by choosing a Visual Studio Release configuration.

The [OSSign signed-package rebuild workflow](https://github.com/OSSign/vadimgrn--usbip-win2/blob/59a28896119cb1729be50d649ae8509180cec24c/.github/workflows/rebuild-from-signed-zip.yml) explicitly replaces the `TEST_SIGNED_DRIVERS` definition with the installer signing directive before compilation. Consequently `INSTALL_TEST_CERTIFICATE` is false and the certificate payload/import branch is excluded. It also enables a signed uninstaller, injects the Microsoft-signed driver files and rebuilds the installer. The exact released artifact's recovered tables agree with that configuration: no developer PFX or import action is present. The workflow inspection establishes the published packaging design; it is not a reproducible-build attestation for this release.

**Consent-review conclusion:** No developer-root certificate installation or automatic weakening of test signing, Secure Boot, signature enforcement or memory integrity was found in the inspected **published 0.9.8.1 x64 installer**. Use only the verified artifact, not an unmodified locally built developer installer or an older signed release. Installation still requires administrative consent, changes drivers/services, restarts USB hubs and requests a system restart (`AlwaysRestart=yes` in upstream setup source). If an installation unexpectedly asks to enable test signing, import a development root or weaken a protection, stop and re-check the artifact rather than following that request.
