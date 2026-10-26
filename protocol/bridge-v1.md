# KeyBridge authenticated USB tunnel, version 1

ADB maps a dynamically allocated Windows loopback port (`adb -s SERIAL forward tcp:0 tcp:37123`) to an Android TLS listener bound to **127.0.0.1:37123**. USB is the default and is verified by `get-devpath`, an explicit ADB USB selector or a present USB PnP serial. The user may explicitly enable already-authorized wireless debugging. Emulators remain excluded. No LAN authenticator listener exists.

## Identity and pairing

Before TLS, Windows sends the fixed eight bytes ASCII `KBTLS1\r\n`; Android must echo exactly those eight bytes. Both then negotiate **mandatory TLS** with ALPN `keybridge/1`. Each preface/handshake is bounded by a 15-second deadline. The preface contains no commands, identities, capabilities or secrets. There is no plaintext operation mode and no downgrade fallback. Any preface/ALPN mismatch closes the channel before application frames are accepted.

This explicit protocol upgrade identifies a binary authenticator transport as such instead of resembling an HTTPS connection to a web/mail inspection proxy. Development testing identified Norton Web/Mail Shield substituting its own certificate for the phone certificate and suppressing TLS client authentication; pinning correctly rejected that substitution. The protocol upgrade does not alter antivirus or Windows settings. Authentication continues to require the actual pinned endpoint identities and full human fingerprint comparison on first use.

TLS 1.2 with ECDHE-ECDSA/AES-GCM and P-256 self-signed transport certificates provides record encryption, fresh session keys, integrity, ordering, and replay protection. TLS session resumption is disabled by the desktop. Android can negotiate TLS 1.3, but Windows/Schannel TLS 1.3 client-certificate interoperability is not verified and the Windows client defaults to TLS 1.2. Transport identities are separate from credential keys. The desktop identity is a non-exportable, per-user Windows CNG key and certificate. The phone transport key lives in Android Keystore.

During first contact, the TLS peer proves possession of an identity that has not yet been trusted. Only Hello and Pair are allowed. Both devices display the complete 64 uppercase hex characters of:

```
SHA256(UTF8("KeyBridge pairing v1") || phoneCertificateDER || desktopCertificateDER)
```

The person must compare both codes, click desktop confirmation, and approve the matching request in the foreground phone application. Phone approval pins the desktop certificate. Desktop confirmation pins the phone certificate after a successful phone response. Different certificates on reconnect fail closed. A phone reset or certificate expiry requires local revocation and re-pairing. ADB authorization is not pairing. A malicious first-contact TLS proxy produces different pairing codes.

The complete SHA-256 fingerprint needs human verification. Compare every group; matching a short prefix is insufficient. It is not truncated because a short static-certificate hash would provide inadequate assurance against simultaneous certificate substitution. Do not approve an unsolicited pairing request. The Android side limits concurrent requests; this initial protocol is not a remotely administered enrollment system.

## Frame

All integer fields are unsigned, big endian. The entire frame is inside TLS.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | ASCII `KBRG` |
| 4 | 1 | Version `1` |
| 5 | 1 | Kind |
| 6 | 2 | Reserved, both zero |
| 8 | 4 | Nonzero request ID |
| 12 | 4 | Payload length, at most 65,536 |
| 16 | length | Payload |

Request IDs strictly increase within one TLS session. Cancel alone reuses the active CTAP request ID. One CTAP transaction is active per authenticator. A response has the same ID and kind OR `0x80`. `0xff` is a protocol error with a UTF-8 description. Malformed headers, over-limit data, reused requests, and response correlation failures terminate the channel. JSON is UTF-8 and bounded by the frame limit. Names are informational, never authorization identities.

| Kind | Request | Response |
| --- | --- | --- |
| 1 Hello | JSON `{hostName}` | JSON `{deviceName, paired, pairingCode, credentialCount, hardwareSecurity, biometricAvailable}`; credential/security/readiness fields appear only for paired hosts |
| 2 Pair | JSON `{hostName}` | JSON `{paired:true}` after explicit foreground phone approval |
| 3 CTAP | command byte + CBOR, 1–7609 bytes | CTAP status byte + optional CBOR, 1–7609 bytes |
| 4 Cancel | empty; ID of active CTAP | CTAP transaction cancelled; desktop closes the channel after sending cancellation |
| 5 Status | empty, requires pairing | safe JSON state, credential count, hardware-security description and biometric availability |
| 6 Ping | arbitrary bounded bytes | same bytes; requires pairing |

Disconnect, timeout, revocation, or cancellation invalidates pending approval. No approval token crosses the wire. CTAPHID keepalives are transport liveness and do not indicate successful approval or biometric verification.

`biometricAvailable` is an optional boolean introduced in 0.1.2. Its absence or a non-boolean value means readiness is unknown, not ready. It reports local biometric capability, not a completed approval; fresh operation-bound approval is still required for signing. Older peers can ignore this additional JSON field.

## Capabilities

The initial authenticator advertises `FIDO_2_0` and ES256 only. No ClientPIN, U2F, CTAP2.1 certification, extensions, or manufacturer attestation is claimed. `none` attestation uses an anonymized all-zero AAGUID; assertion signatures use the phone credential key. Strong biometric approval is required by this implementation. A phone lacking a suitable biometric fails closed.
