# Android development packages

`KeyBridge-debug.apk` is the installable development build, signed with this build environment's Android debug identity. To update the existing development installation while preserving its Keystore credentials, use `adb install -r KeyBridge-debug.apk` with the same signing identity. Do not uninstall or reset the phone application to resolve an update signature mismatch: that destroys its credential keys.

`KeyBridge-release-unsigned.apk` is the release variant compiled successfully without a production signing identity. It is **unsigned and cannot be installed as supplied**. Sign with your own protected release key using Android's official APK signing tools before distribution. It cannot update the debug installation unless signed with that installation's original identity.

Both packages are version 0.1.2, application ID `dev.keybridge.android`, minimum Android 11/API 30. Hardware-backed Keystore availability and strong biometrics are checked on the device. This development release is not FIDO certified or independently security audited. See the Windows package's `docs/verification.md` for executed tests and remaining limitations.
