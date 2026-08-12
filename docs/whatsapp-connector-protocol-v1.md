# WhatsApp bridge protocol v1

The transport remains newline-delimited JSON over child-process stdin/stdout.
No existing command or event is renamed or removed.

## Envelope

Request:

```json
{"command":"ping","requestId":"opaque-id"}
```

Success response:

```json
{"type":"response","requestId":"opaque-id","ok":true,"result":{}}
```

Failure response:

```json
{"type":"response","requestId":"opaque-id","ok":false,"result":null,"error":{"code":"stable_code","message":"bounded_message"}}
```

Event:

```json
{"type":"event","event":"connection","accountId":"local-account-id","data":{}}
```

## Handshake metadata

`ready`, `ping`, and `initialize` include additive metadata:

```json
{
  "bridge":"WAFlow.WhatsApp.Bridge",
  "bridgeVersion":"0.9.0",
  "protocolVersion":1,
  "connector":"baileys",
  "connectorVersion":"7.0.0-rc13",
  "capabilities":{
    "multiAccount":true,
    "qrPairing":true,
    "sessionPersistence":true,
    "directMessages":true,
    "groupMessages":true,
    "historySync":true,
    "offlineCatchup":true,
    "mediaReceive":true,
    "textSend":true,
    "mediaSend":true,
    "reply":true,
    "revoke":true,
    "deliveryReceipts":true,
    "readReceipts":true,
    "numberValidation":true,
    "pinChat":true,
    "groups":true,
    "labels":true,
    "lidMapping":true,
    "outboundGovernor":true,
    "idempotency":true
  }
}
```

An absent metadata block is a legacy bridge, not an instruction to log out or
drop a session. Relvyn supplies the known legacy capability set, marks protocol
confidence degraded, and preserves connection/read behavior.

## Commands retained in v1

`ping`, `initialize`, `configure_outbound`, `outbound_status`, `connect`,
`validate_session`, `disconnect`, `logout`, `send_text`, `send_media`,
`validate_number`, `revoke_message`, `set_chat_pin`, `create_group`, `sync_now`,
`label_upsert`, `label_create`, `chat_label_set`, and `catch_up_history`.

## Events retained in v1

`ready`, `auth_recovery`, `qr`, `connection`, `connection_stage`,
`connection_issue`, `bridge_error`, `sync_status`, `contacts_upsert`,
`chats_upsert`, `messages_history`, `message`, `message_revoked`,
`message_status`, `label_upsert`, `chat_label_upsert`, `group_created`, and
`outbound_suspended`.

Additional events and fields must be ignored by old readers. A future breaking
change requires a new integer `protocolVersion`; it cannot be smuggled into v1.
