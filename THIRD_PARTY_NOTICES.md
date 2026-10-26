# Dependencies and reference research

KeyBridge's new source is MIT licensed. Reference source was inspected before implementation; **no source from the unlicensed keinPlan/virtual-fido tree was copied**. Its bundled driver installer was not executed. KeyBridge's C# USB transport was implemented against the published wire protocols; Android uses the platform security APIs.

| Component/reference | License / treatment |
| --- | --- |
| bulwarkid/virtual-fido | MIT; USB/IP/FIDO architecture reference, not bundled |
| keinPlan/virtual-fido | No root license found in inspected revision; inspection only, no source reuse |
| vadimgrn/usbip-win2 | BSD-2-Clause; all-in-one setup embeds the unchanged official signed 0.9.8.1 installer, SHA-256 pinned and Authenticode checked. Its release license is installed under `licenses/usbip-LICENSE.txt`; the upstream wizard retains its component notices |
| .NET / WPF / System.Formats.Cbor | Microsoft open-source runtime/library licenses; runtime notices included by self-contained publish |
| Kotlin, Coroutines | Apache-2.0 |
| AndroidX Compose, Activity, Biometric, lifecycle | Apache-2.0 |
| Gradle wrapper/distribution | Apache-2.0 |
| Android Debug Bridge 37.0.1 / Windows libraries | Selected unchanged ADB files from official Platform Tools, with the complete package `NOTICE.txt` and `source.properties` under `platform-tools`. Open-source component licenses govern these files; no Android SDK platform, proprietary Google APIs, fastboot or filesystem tools are bundled |
| Inno Setup | Official signed 6.7.3 compiler; original engine/license, installed under `licenses/InnoSetup.txt`. Compiler is a build dependency, not installed on the target PC. Commercial users must review the upstream commercial licensing terms |

See `android/app/build.gradle.kts`, `android/core/build.gradle.kts`, and the Windows project files for exact direct versions. Consult each dependency's source and package notices before redistributing binaries. Reference specifications are linked, not reproduced. All visual illustrations/icons drawn in this repository are project-native vectors; Windows uses its system glyph fonts under the operating system's font terms.
