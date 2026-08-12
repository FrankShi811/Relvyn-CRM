# WhatsApp current behavior baseline

This document freezes the user-visible WhatsApp behavior shipped in Relvyn
5.21.1. Connector resilience work may add observability and fail-safe guards,
but it must not remove, rename, or silently change any behavior below.

## Account and connection lifecycle

- Multiple local WhatsApp accounts can be configured and run independently.
- A new account pairs with the QR code emitted by WhatsApp. The QR flow and
  pairing timeout remain unchanged.
- Session material is stored below `whatsapp-sessions/<account-id>` and is
  encrypted with a per-account key in Windows Credential Manager under
  `WAFlow/WhatsAppSessionKey/<account-id>`.
- A valid encrypted session is reused after an application restart and after a
  normal automatic update. An unreadable local session is backed up and the
  user is asked to pair again instead of silently deleting the only copy.
- Manual disconnect suppresses automatic reconnect for that account. Connect
  re-enables it. Logout asks WhatsApp to unlink, terminates the account bridge,
  clears only that account's local session, and then permits a new QR pairing.
- Unexpected disconnects retry automatically unless the user disconnected.
  Rate-limit suspension and retry delay are respected.
- Windows system proxy and supported HTTP/SOCKS proxy routes are used when
  available. An explicitly permitted direct fallback is attempted when the
  proxy cannot produce a QR.
- Protocol version discovery prefers WhatsApp Web, then Baileys, then the last
  successful local cache, then the bundled validated version. Network failure
  does not erase the cache or force a session reset.
- Fresh pairings use the Windows Chrome/Web profile. A session proven to use
  the Desktop history profile may reconnect with full-history support. Failed
  Desktop-profile upgrades fall back without discarding the accepted session.

## Inbox receive, history, and identity

- Direct chats and participating group chats are discovered and synchronized.
- Live messages, history sync, embedded chat anchors, reconnect catch-up, and
  manually requested offline catch-up are ingested without duplicating known
  provider message IDs.
- Contacts, chats, groups, unread counts, pinned state, last-message preview,
  delivery/read status, quoted-message context, and revocations remain visible.
- Text, image, video, audio, document, sticker, contact, location, poll,
  reaction, and event content are normalized. Unsupported or not-yet-recovered
  content uses an explicit placeholder rather than disappearing.
- Media is downloaded to the local workspace with bounded names and recovery
  support. A media failure does not erase the message shell.
- LID and phone-number JIDs are resolved and linked. Direct-chat identity and
  group participant identity remain separated, including participant names and
  phone mapping when WhatsApp exposes it.
- Status broadcasts and unsupported inbound JIDs are excluded from customer
  conversation routing.
- Synced messages continue feeding unread summaries, opportunity analysis,
  Customer Brain, customer identity, follow-up tasks, and Customer Success
  Agent inputs through the existing repository and event pipeline.

## Sending and message actions

- A human can send text, supported media, replies to text/media, and revoke a
  sent direct-chat message from the Inbox.
- Customer Success Agent and campaigns use the same connector with explicit
  origins, budgets, idempotency keys, and their existing authorization gates.
- A send verifies connection state, catch-up state, account budget, target
  number/JID, sender/recipient device fanout, and the provider result target.
- A missing or uncertain acknowledgement is pending, never permission to send
  the same automatic action again. Existing idempotency replay remains the
  duplicate-send guard for RPC timeouts.
- Number registration lookup remains available and fails closed when WhatsApp
  is not connected.
- Human, AI-auto, and campaign traffic retain independent daily caps plus the
  shared cap, minimum gap, jitter, persisted counters, and 403/429 suspension.
- Automatic sends remain blocked while offline-history catch-up is active.

## Chats, groups, pins, labels, and lists

- Direct chats can be pinned or unpinned and the result remains synchronized.
- Groups can be created with validated participants and appear in the Inbox.
- WhatsApp labels/custom lists can be read, created, renamed, recolored,
  removed, assigned, and unassigned through server-confirmed regular App State.
- Label mutations preserve current custom-label identifiers, including the
  cleanup path for legacy local GUID labels.
- Phone/LID association replay keeps mobile-created label assignments attached
  to the same customer chat.

## Automation and cross-module behavior

- Campaign automation retains its scheduling, per-recipient status, public-IP
  safety check, outbound caps, retry behavior, and cross-account selection.
- Customer Success hosting retains explicit start/stop ownership, human
  handoff, restart recovery, identity locks, pre-send/post-send validation,
  audit events, and idempotent automatic replies.
- Sourcing requests, knowledge retrieval, translation, AI suggestions, and
  Customer Brain remain downstream consumers. Connector degradation must not
  disable local CRM, email, knowledge, analysis, or customer data access.
- An account-level WhatsApp failure must not stop other WhatsApp accounts.
- A feature-level failure must not turn a healthy read path into a global
  "disconnected" state.

## Upgrade invariants

- The database path and schema remain compatible and migrations remain
  additive, idempotent, and transactional.
- `waflow.db`, customer/Buyer ID data, email data, knowledge base, Customer
  Brain, tasks, settings, API credential targets, and encrypted WhatsApp session
  paths are preserved across update.
- Package identity, AppUserModelID, local workspace identity, updater feed, and
  Windows Credential Manager target names remain unchanged.
- No connector compatibility check may delete, rewrite, move, or log out a
  stored session.

## Explicitly deferred from the automated gate

- Real-account WhatsApp canary E2E is manual because CI must never receive a
  production QR code or session secret.
- Meta Cloud API, bridge hot-update/switching, and a long-lived Baileys fork are
  not part of this resilience layer.
