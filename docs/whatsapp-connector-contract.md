# WhatsApp connector contract

## Boundary

`IWhatsAppConnector` is an additive facade over the existing
`WhatsAppConnectionManager`. Existing callers, public methods, view models,
database tables, RPC commands, and user workflows remain valid. The facade is
the seam for future connector implementations; it is not a rewrite of the
current Baileys path.

The stable connector must provide:

- account lifecycle: start, connect, disconnect, logout, session recognition;
- read/sync: live events, cached snapshots, manual sync, history catch-up;
- outbound: number validation, text/media/reply, revocation, group creation;
- chat metadata: pin and WhatsApp label/custom-list operations;
- protocol metadata, capability negotiation, feature health, safe-mode state,
  and redacted diagnostic export.

All legacy APIs on `WhatsAppConnectionManager` continue to call the same
`WhatsAppBridgeClient` methods. New metadata is optional on the wire so a
locally selected older bridge remains readable and connectable.

## Failure isolation

Connector health is tracked per account and per feature. Valid states are:

- `Healthy`: the capability is advertised and no current fault is known;
- `Degraded`: the path remains usable with reduced confidence or fallback;
- `Unavailable`: the capability is absent or its operation cannot run;
- `Suspended`: a safety or provider throttle intentionally blocks the path;
- `Unknown`: the bridge has not reported enough evidence yet.

A label error degrades labels, a media error degrades media, and a history error
degrades history. None of those alone changes the transport connection to
disconnected. Transport/session failures may affect all WhatsApp features for
that account, but never another account or non-WhatsApp modules.

## Safe mode

Safe mode is account scoped. It can be entered by duplicate-send suspicion,
target-verification failure, incompatible protocol, uncertain automatic-send
recovery, repeated rate limits, or a connector compatibility rejection.

Safe mode keeps receive, local history, Customer Brain, CRM, email, knowledge,
and analysis available. It blocks dangerous automatic text/media sends and may
block a specifically unsafe mutation capability. A human send is not disabled
for an unrelated automatic-send fault; target/protocol safety still fails
closed at the existing connector validation boundary.

The embedded feature policy defaults every 5.21.1 behavior to enabled. A scoped
policy may only disable named risky capabilities. It cannot execute code, read
secrets, upload data, or expand permissions. A missing/unreadable policy keeps
the last accepted local policy, otherwise the all-enabled embedded default.

## Diagnostics

Diagnostics are written to a user-selected ZIP. Allowed content is limited to
application/bridge/protocol/connector versions, capability and feature-health
snapshots, safe-mode reasons, bounded redacted error codes, database integrity,
and operating-system/runtime summary. Account IDs are one-way hashed locally.

The archive must never contain customer names, phone numbers, Buyer IDs,
message text, media/file paths, API keys, tokens, credentials, environment
values, QR data, session directories, encrypted session files, or Windows
Credential Manager target contents.
