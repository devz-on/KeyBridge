# Threat model and limits

## Protected assets and decisions

Credential private keys must remain on Android. Every registration/assertion needs local strong-biometric approval. A paired host must not enroll another host, bypass approval, export a key, change biometric requirements or suppress the RP shown on the phone. Raw CTAP data, user handles and key material must not appear in Windows logs or diagnostic exports.

The desktop and phone OS, Android app package and biometric implementation are trusted to enforce their APIs. A compromised Android application/OS or Keystore implementation is outside the protection offered by this app. Hardware backing is probed and reported; StrongBox is not assumed. Software-backed devices are identified honestly. This version refuses sensitive operations without an enrolled strong biometric.

## Mitigations

- ADB authorization alone grants no FIDO use. Both devices explicitly pair over mutual TLS and pin exact certificate fingerprints. A comparison code binds both certificates and detects a first-contact proxy when the person compares both displays.
- Listeners bind loopback only. Windows verifies the default USB transport; already-authorized wireless debugging requires explicit opt-in, and emulators are excluded. ECDHE-ECDSA/AES-GCM TLS provides authenticated encryption, ephemeral session secrets, ordered records and record replay protection. The app never creates a phone LAN authenticator endpoint.
- Versioned bounded frames, increasing request IDs, strict response correlation, one CTAP transaction, timeouts, queue limits and cancellation prevent stale operation reuse and uncontrolled allocation.
- A fresh biometric CryptoObject authorizes each credential-key signature. Android Keystore marks private keys non-exportable, unlocked-device-only and invalidated by biometric enrollment changes. No reusable approval token crosses the bridge.
- App-private metadata is integrity authenticated with an Android Keystore HMAC key. Credential counters reserve a value durably before signing; cancellation may create gaps, never reuse a live value.
- Pair revocation closes matching sessions. Reset/deletion requires explicit local confirmation and strong biometric approval. Background operation is visible and user started; requests never force an activity over Android background restrictions.
- USB/IP is unencrypted locally because it carries the standard Windows virtual-bus protocol. It is restricted to loopback and does not itself grant a phone signing operation without pairing and per-use approval.

## Residual risks and incomplete protections

A paired compromised desktop can choose arbitrary RP IDs/client-data hashes, make unsolicited requests, lie about its friendly name, or cause denial of service. Check the requested RP/account and initiate the browser operation yourself. The phone does not receive a trustworthy complete browser origin.

The full SHA-256 pairing fingerprint needs deliberate comparison of all 64 hexadecimal characters; a matching short prefix or unattended first-use trust is unsafe. The initial short code was removed during review because static-certificate substitution can exploit a truncated comparison's collision bound. A formal pairing-ceremony review remains necessary. Other local desktop processes may contact the USB/IP listener and cause traffic or denial of service, although they cannot sign without phone approval. TLS bootstrap temporarily accepts unpaired self-signed identities for Hello/Pair; it does not confer privileged access. Local application pins/preferences rely on the Windows account's storage protection. Windows malware running as that account can use its transport identity; phone approval remains necessary.

SQLite integrity does not provide hardware rollback protection. A root/backup-level attacker capable of replacing a previously authenticated database snapshot could roll metadata/counters back. Android backup is disabled; there is no cross-device sync or credential recovery. Deletion is key removal and logical metadata deletion, not a promise of forensic flash erasure.

The external USB/IP driver adds kernel attack surface. Its installer signature and advertised compatibility are distinct from kernel-catalog verification and testing on this Windows build. No binary kernel-driver compatibility is certified by this repository. Development VID/PID `1209:0001` must be replaced by an allocated identifier before production distribution.

No CTAP2.1, ClientPIN, U2F, extensions, platform passkey plugin, FIDO certification or security audit is claimed. Some Windows/browser/RP policies may require unsupported features. Hardware acceptance and all remaining unsupported controls are listed in `docs/verification.md`.
