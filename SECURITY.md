# Security Policy

Ctrl Link moves clipboard contents and files between devices, so we take security reports seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private
[security advisory](../../security/advisories/new) form on this repository instead.
Include the platform and version, steps to reproduce, and the impact you expect.

You'll get an acknowledgement within a few days, and a fix or mitigation plan once the issue is confirmed.

## Scope

- The wire protocol and its cryptography (`Codebase/protocol/PROTOCOL.md`)
- Pairing (QR / 6-digit code), session establishment and transfer handling in the Mac, Android and Windows apps
- Local storage of pairing keys (macOS: owner-only 0600 file, Android: Keystore-encrypted, Windows: DPAPI-encrypted)

## Design summary

- Pairing: X25519 key agreement bound to the QR token or 6-digit code via HKDF, with MAC confirmation.
- Sessions: per-connection keys derived from the pair key, AES-256-GCM with direction-separated nonces.
- Traffic stays on the local network. There are no servers, accounts or telemetry.
