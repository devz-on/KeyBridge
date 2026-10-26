# USB/IP implementation and driver research

Research date: 2026-10-26. Hardware enumeration is **not verified** on this development computer. Its OS reports Windows 11 Pro x64 build 26300, and the environment inspection found no installed USB/IP VHCI device. No kernel driver was installed, no downloaded installer was executed, and no boot/security settings were changed during USB component development.

## References and legal choices

* [bulwarkid/virtual-fido](https://github.com/bulwarkid/virtual-fido) is MIT licensed (copyright 2022 BulwarkID). Its Go architecture exports an emulated authenticator through a local USB/IP server. It was studied as a reference for the device/export boundary. KeyBridge's C# implementation does not include copied Go source or the upstream Windows credential store.
* [keinPlan/virtual-fido](https://github.com/keinPlan/virtual-fido), inspected at commit `6ad72d2f76696abd1c94dd1ab006e0a88f0b859b`, contains C# HID/USB/IP and VHCI integration. No license grant was present in the inspected checkout, so its source and bundled driver installer were not reused. The presence of an installer in a repository does not establish its signature, provenance, or compatibility.
* [vadimgrn/usbip-win2](https://github.com/vadimgrn/usbip-win2), inspected at commit `9ff1fe0c71979e5b26c69f5b8dd4b1eeef6b7578`, is BSD-2-Clause licensed. It supplies a USB/IP client and UDE/VHCI drivers. Upstream documents Windows x64 version 1903 or later and WHLK/attestation-signed releases. These are upstream support statements, not a local verification of a particular binary on build 26300. KeyBridge does not bundle a kernel driver.
* The authoritative wire references are the [Linux USB/IP protocol documentation](https://www.kernel.org/doc/html/latest/usb/usbip_protocol.html) and [FIDO CTAP USB binding](https://fidoalliance.org/specs/fido-v2.2-ps-20250714/fido-client-to-authenticator-protocol-v2.2-ps-20250714.html#usb).

The selected approach is a small independently testable C# USB/IP export with an **external, separately verified USB/IP client driver**. Reusing the kernel-side USB/IP infrastructure avoids developing a new Windows kernel driver; the authenticator signing engine remains exclusively on Android.

## Implemented boundary

`windows/KeyBridge.Usb` implements:

* USB/IP 1.1.1 DEVLIST, IMPORT, SUBMIT/RET_SUBMIT and UNLINK/RET_UNLINK.
* One exclusive imported device, bounded management connections and interrupt IN requests, exact stream reads, strict size limits, and loopback-only listening.
* Full-speed USB HID descriptors, input/output interrupt endpoints with 64-byte reports, endpoint-zero standard descriptor/configuration requests, HID report descriptor and SET_REPORT/IDLE behavior.
* FIDO usage page `0xF1D0`, usage `0x01`; CBOR and NMSG capabilities; INIT, PING, CBOR, CANCEL, KEEPALIVE and ERROR.
* Bounded channel allocation, 7609-byte messages, continuation sequence validation, packet/operation timeouts, exclusive CTAP transactions, resynchronization and cancellation.

It does not implement CTAP1/U2F, WINK, LOCK, boot HID, isochronous USB, arbitrary USB-device forwarding, or credential cryptography. Unsupported legacy MSG receives CTAPHID INVALID_CMD. It does not fabricate a successful CTAP response on transport failure.

Development VID/PID is `1209:0001`, a development/test identity. Obtain an appropriate assigned VID/PID before distributing a production device identity. The USB manufacturer/product strings identify KeyBridge and do not impersonate another authenticator vendor. USB/IP import alone does not prove Windows HID recognition; HID recognition alone does not prove browser WebAuthn interoperability.

## Driver verification and installation policy

Use only the official upstream release source. Before installation, record the release version, source URL, SHA-256 digest, Authenticode result, certificate publisher/chain, catalog/driver package signature, and the target Windows build. Reject an unsigned, invalidly signed, or unexpectedly published package. Check upstream compatibility notes and confirm installation with Secure Boot and driver-signature enforcement remaining enabled. Administrative installation requires the user's explicit consent.

Upstream documentation also contains test-signing instructions for developer builds. Those instructions are **not** part of KeyBridge's supported installation path. Do not disable Secure Boot, enable test signing, or weaken Windows protections to make an unverified package load.

The external client attaches bus `1-1` from `127.0.0.1:3240`. Because driver versions may vary, use the installed client's documented CLI syntax. Disconnecting or stopping the local server closes imported USB/IP sessions and cancels pending Android operations; do not detach unrelated USB/IP devices during cleanup.

## Executed tests and remaining verification

Executed on Windows x64 using .NET SDK 10.0.403:

```powershell
dotnet run --project tests/KeyBridge.Usb.Tests/KeyBridge.Usb.Tests.csproj -c Release
```

Result: **24/24 passed**. Tests exercise CTAPHID boundaries/state/cancellation and real loopback TCP USB/IP exchanges, including descriptors, exclusive import, chunked requests, interrupt transfers, unlink suppression, malformed transfer rejection, undrained-output backpressure and shutdown cancellation. Test transports are confined to the test executable and are not used in production.

Subsequent hardware verification on 2026-10-26: the user-approved, separately verified usbip-win2 0.9.8.1 driver loaded on build 26300.9711, Windows enumerated a healthy FIDO HID, and real Chrome/localhost registration and authentication passed using fresh approvals on the physical Android phone over authorized wireless ADB. The browser verified the ES256 assertion and counter 2. Actual ADB loss removed the virtual HID, and pinned reconnection succeeded. See [verification.md](verification.md) and [driver-component-verification.md](driver-component-verification.md).

Still unverified: physical cable unplugging during authentication, suspend/resume, other Windows builds/browsers/relying parties and long-duration driver stress testing. Software test results alone do not establish these hardware acceptance criteria.
