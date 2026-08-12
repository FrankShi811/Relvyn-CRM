# Baileys version policy

## Current channels

- Stable: `@whiskeysockets/baileys` `7.0.0-rc13`, the exact dependency shipped
  with the current connector.
- Previous: the exact Stable version from the immediately preceding Windows
  release. Previous stays available as source/tag/build evidence, not as a
  runtime switch downloaded to user machines.
- Candidate: a proposed exact version evaluated only in an isolated branch and
  CI/manual canary. Candidate is never selected for ordinary users.

Ranges, floating tags, and install-time dependency resolution are prohibited in
release builds.

## Promotion gate

Candidate can become Stable only after:

1. license and source-distribution review;
2. frozen-lockfile install and complete Bridge unit/RPC/replay suite;
3. all connector compatibility tests, desktop smoke tests, installer tests,
   upgrade/data/session-layout tests, and updater tests pass;
4. QR/session/history/labels/pin/media/target verification/governor/agent and
   campaign behavior show no regression;
5. a manual canary account completes pair, restart, reconnect, receive, send,
   history, group, label, and logout checks without production data entering CI.

If any gate fails, Stable remains unchanged. A protocol lookup failure at
runtime uses the last known or bundled WhatsApp Web protocol version and does
not change the installed Baileys package.
