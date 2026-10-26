# Contributing to KeyBridge

KeyBridge combines a Windows USB/IP bridge and an Android authenticator. Contributions are welcome across the UI, transport, documentation, accessibility and compatibility testing.

## Before changing code

Check existing issues and open a short proposal for a new protocol capability or a larger architectural change. Bug fixes should include a reproducible case. Security vulnerabilities belong in [private reporting](https://github.com/devz-on/KeyBridge/security/advisories/new), not public issues.

Fork the repository, branch from `main` and keep each pull request focused. For a separate checkout, use:

```sh
git worktree add ../KeyBridge-fix -b fix/short-description
```

See the README for .NET, Java, Android SDK and Inno Setup prerequisites. Keep SDK paths in local configuration, not in committed source.

## Build and check

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
```

The test script runs Windows USB/IP and transport suites, Android JVM tests, browser helpers and the loopback diagnostic server checks. `-SkipAndroid` and `-SkipBrowser` are available for a deliberately narrower run; state that scope in the pull request.

Hardware instrumentation is opt-in (`Test.ps1 -Device`). Use a dedicated test device and synthetic accounts. A test runner can restart or replace app packages; do not run it against credentials you rely on. Never automate fingerprint approval, remove Keystore requirements or weaken platform protections to obtain a passing result.

For UI changes, inspect the affected screens with normal and larger text sizes, keyboard navigation on Windows and system Back on Android. For connection changes, check both failure/recovery and the successful path. Do not report a listening socket as verified Windows HID enumeration or a passing unit test as verified phone signing.

## Boundaries to preserve

- Credential private keys and approval stay on Android. Exportable keys belong only in test fixtures.
- Pairing, pinning, request correlation, cancellation and size limits fail closed.
- Driver/client signature checks remain enforced. Do not add test-signing, antivirus or Secure Boot workarounds.
- Protocol changes update the contract and shared vectors. Advertise a CTAP capability only when it is implemented and tested.
- Counter/storage changes need failure-case coverage, including interrupted operations.

## Pull requests

Explain the problem, the resulting behavior, relevant tradeoffs and checks actually run. Include hardware/browser versions for integration results while omitting serials, network addresses, account names and raw CTAP data. Mention untested cases directly. Screenshots should contain no pairing codes or personal data.

Do not commit generated packages, SDKs, signing material, local settings, credentials or device dumps. Release binaries belong in GitHub Releases. Contributions are provided under the project's MIT license; third-party code must have a compatible license and preserved notices.

## Useful areas

Fresh Windows installations, additional browsers/relying parties, suspend/resume, disconnect during approval, accessibility, large-text layouts, Android vendor behavior and long-running sessions all need broader evidence. The current [verification record](docs/verification.md) identifies remaining gaps.

Maintainers review changes for correctness, test evidence, scope and security impact. A working reproduction or a careful documentation correction is as useful as a new feature.
