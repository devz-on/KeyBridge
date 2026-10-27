# Android release signing

The official APK is built from the `release` variant, with `android:debuggable` disabled, and signed by **CN=KeyBridge Release, O=KeyBridge** using a dedicated RSA 3072-bit key. It is separate from Android's debug identity. This signing certificate identifies compatible updates; it does not imply a security audit, FIDO certification or Play Store distribution.

Release certificate SHA-256:

```text
A95FBB8CBD9CF0D2ABB33C8842BA1B1C516DD3D9F3F9F8C31D1CF19D4B4D3649
```

The public certificate is [`assets/android-release-certificate.der`](../assets/android-release-certificate.der). The proof of signing-key rotation is [`assets/android-release.lineage`](../assets/android-release.lineage). These contain public certificates and signatures, never private keys or passwords.

## Verify a download

Use Android SDK Build Tools 36.0.0 or newer:

```text
apksigner verify --verbose --print-certs KeyBridge-0.1.2-android-release.apk
aapt2 dump badging KeyBridge-0.1.2-android-release.apk
```

The first command must succeed, report v3 signing and show the release certificate fingerprint above. The second must show package `dev.keybridge.android`, version code `4`, version `0.1.2`, and no `application-debuggable` line. Check the APK's file hash against the release's `SHA256SUMS.txt` as well.

## Updating the original development installation

The official APK carries a v3 signing lineage from the original maintainer development certificate (`4801AC5C8F0E14DD47368F3AB93F1368F86EDCE59444E7AEFF2C60C216F42C9E`) to the release key. The predecessor authorizes installed-data migration; rollback to that signing key is disabled. The new release key signs the APK on all supported Android versions (API 30+). No debug signing key is included in a download.

Use `adb install -r` to preserve the app's UID, data and Keystore entries. The signature and lineage are verified locally. The first physical migration is still pending because the test phone was unavailable during release packaging. If Android rejects the update, stop and inspect the error; do not uninstall or clear app data. Builds signed by another developer's debug key are outside this lineage.

## Maintainer procedure

Build unsigned release input with `android/gradlew.bat :app:assembleRelease`. Keep the release keystore outside the checkout and supply the following environment variables only to the signing process:

| Variable | Purpose |
| --- | --- |
| `KEYBRIDGE_ANDROID_KEYSTORE` | Path to the private release keystore |
| `KEYBRIDGE_ANDROID_KEY_ALIAS` | Release key alias |
| `KEYBRIDGE_ANDROID_STORE_PASSWORD` | Keystore password |
| `KEYBRIDGE_ANDROID_KEY_PASSWORD` | Key password, if different |
| `KEYBRIDGE_ANDROID_SIGNING_LINEAGE` | Public lineage file, when preserving existing rotation history |
| `KEYBRIDGE_ANDROID_CERT_SHA256` | Expected certificate fingerprint; set for official builds |

Use a password manager or secret store to populate passwords. Do not put them in command history, checked-in Gradle properties, logs or workflow files.

Run `scripts/Sign-AndroidRelease.ps1`, or `scripts/Package.ps1 -SignAndroid` for the complete package. The signing script refuses a debuggable input or Android Debug certificate and verifies its output. The installer builder independently verifies the release APK before embedding it. It never substitutes a debug APK when release signing is unavailable. CI compiles unsigned release input and tests the code without access to the private signing key.

The first maintainer key is stored outside this repository, with its password protected by Windows DPAPI. That encrypted password is tied to its Windows user and machine. Keep an encrypted offline backup of the private keystore **and a recoverable password**; a copied DPAPI file alone cannot recover the key on another computer. Losing either prevents signing future compatible updates. Contributors should use their own identity for local builds and leave the official public certificate/lineage unchanged.

The Windows setup is still unsigned. Its embedded usbip-win2 driver retains the upstream publisher's signatures. Android APK signing and Windows Authenticode are separate release processes.

References: [Android apksigner](https://developer.android.com/tools/apksigner), [APK signature scheme v3](https://source.android.com/docs/security/features/apksigning/v3).
