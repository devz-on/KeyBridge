# Security policy

KeyBridge is a development authenticator. It is not FIDO certified or independently security audited. Keep an alternative recovery method and use diagnostic accounts while evaluating it. The current verification scope is recorded in [docs/verification.md](docs/verification.md).

## Reporting a vulnerability

Use [GitHub's private vulnerability reporting](https://github.com/devz-on/KeyBridge/security/advisories/new). Include the affected version or commit, component, expected trust boundary, impact and a minimal reproduction using synthetic data. Do not publish an authorization bypass or user data in a public issue.

Do not attach credential databases, account identifiers, raw CTAP requests, biometric information, debugging authorizations or certificates containing private keys. Redact device serials, network addresses, pairing fingerprints and local user paths from screenshots and logs.

The project currently maintains the latest development version. There is no published response-time guarantee or formal bug-bounty program. Reports will be assessed before a fix or advisory is released; please coordinate disclosure through the private report.

## Security model

Credential keys are generated and used exclusively by Android Keystore with fresh biometric authorization. Windows stores a separate non-exportable CNG transport identity, public phone pins and preferences. The phone trusts the paired desktop as a client; it does not independently verify the browser origin. `none` attestation does not identify or attest the hardware to a relying party.

Driver attachment is enabled by default, but still requires a valid client signature, signed installed driver and paired phone. This preference does not approve a phone operation. Driver installation remains an interactive administrator action after the companion installs. No protection-setting change is part of the supported installation path.

Development APKs use debug signing. Release signing keys must stay outside Git. A compromised signing key, paired computer or unlocked device is a material trust-boundary concern.

## If a boundary fails

Stop the bridge, disconnect the phone and revoke the computer locally. Avoid the affected credential and use the relying party's alternate sign-in/recovery method. Uninstalling or resetting Android destroys its Keystore credentials; it does not recover an account. No private-key export or cloud recovery exists.
