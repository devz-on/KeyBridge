# Shared contract

The authoritative cross-language wire contract is `protocol/bridge-v1.md`. Windows uses `BridgeProtocol.cs`; Android uses `core/Frame.kt`. Neither platform shares credential private keys or serialized approval tokens.

`frame-v1.json` is a small deterministic interoperability vector. Payload `04` is the real CTAP authenticatorGetInfo command, not a successful authentication response. Bytes are hex, integer fields use network order.
