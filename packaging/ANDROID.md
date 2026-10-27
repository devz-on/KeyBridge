# Android packages

`KeyBridge-release.apk` is the distributable release variant, signed with a dedicated KeyBridge release key and with debugging disabled. The standalone GitHub download is named `KeyBridge-0.1.2-android-release.apk`; Windows setup includes the same bytes. Certificate SHA-256: `A95FBB8CBD9CF0D2ABB33C8842BA1B1C516DD3D9F3F9F8C31D1CF19D4B4D3649`.

The official APK contains a signing lineage from the original maintainer development identity to the release key. Use `adb install -r KeyBridge-release.apk` to update in place. The lineage is verified locally; migration on a physical phone remains untested. Do not uninstall/reset to resolve a signature mismatch: that destroys credential keys. Unrelated debug builds cannot migrate using this lineage.

Local developer builds may also produce `KeyBridge-debug.apk` and `KeyBridge-release-unsigned.apk`. These are not the release download; an unsigned APK cannot be installed. See `docs/release-signing.md` for build/signing instructions and public certificate/lineage files. Private release keys are never included.

The release is version 0.1.2 (version code 4), application ID `dev.keybridge.android`, minimum Android 11/API 30. Hardware-backed Keystore availability and strong biometrics are checked on the device. KeyBridge is not FIDO certified or independently security audited. See `docs/verification.md` for executed tests and remaining limitations.
