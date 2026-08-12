// SPDX-License-Identifier: GPL-3.0-only

import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  BRIDGE_NAME,
  BRIDGE_VERSION,
  CONNECTOR_NAME,
  CONNECTOR_VERSION,
  PROTOCOL_VERSION,
  STABLE_CAPABILITIES,
  connectorMetadata
} from '../src/connector-protocol.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..')
const packageJson = JSON.parse(await fs.readFile(path.join(root, 'package.json'), 'utf8'))
const manifest = JSON.parse(await fs.readFile(path.join(root, 'compatibility-manifest.json'), 'utf8'))
const indexSource = await fs.readFile(path.join(root, 'src', 'index.mjs'), 'utf8')

assert.equal(BRIDGE_NAME, 'WAFlow.WhatsApp.Bridge')
assert.equal(BRIDGE_VERSION, packageJson.version)
assert.equal(PROTOCOL_VERSION, 1)
assert.equal(CONNECTOR_NAME, 'baileys')
assert.equal(CONNECTOR_VERSION, packageJson.dependencies['@whiskeysockets/baileys'])
assert.equal(manifest.bridgeVersion, BRIDGE_VERSION)
assert.equal(manifest.protocolVersion, PROTOCOL_VERSION)
assert.equal(manifest.connector, CONNECTOR_NAME)
assert.equal(manifest.connectorVersion, CONNECTOR_VERSION)
assert.equal(manifest.channel, 'stable')
assert.equal(manifest.defaultCapabilitiesEnabled, true)

const expectedCapabilities = [
  'multiAccount', 'qrPairing', 'sessionPersistence', 'directMessages',
  'groupMessages', 'historySync', 'offlineCatchup', 'mediaReceive',
  'textSend', 'mediaSend', 'reply', 'revoke', 'deliveryReceipts',
  'readReceipts', 'numberValidation', 'pinChat', 'groups', 'labels',
  'lidMapping', 'outboundGovernor', 'idempotency'
]
assert.deepEqual(Object.keys(STABLE_CAPABILITIES), expectedCapabilities)
assert.ok(Object.values(STABLE_CAPABILITIES).every(Boolean), 'stable must advertise every existing behavior')
assert.deepEqual(connectorMetadata('connected').capabilities, STABLE_CAPABILITIES)

const legacyCommands = [
  'ping', 'initialize', 'configure_outbound', 'outbound_status', 'connect',
  'validate_session', 'disconnect', 'logout', 'send_text', 'send_media',
  'validate_number', 'revoke_message', 'set_chat_pin', 'create_group',
  'sync_now', 'label_upsert', 'label_create', 'chat_label_set',
  'catch_up_history'
]
for (const command of legacyCommands)
  assert.match(indexSource, new RegExp(`case ['"]${command}['"]`), `missing legacy command ${command}`)

const legacyEvents = [
  'ready', 'auth_recovery', 'qr', 'connection', 'connection_stage',
  'connection_issue', 'bridge_error', 'sync_status', 'contacts_upsert',
  'chats_upsert', 'messages_history', 'message', 'message_revoked',
  'message_status', 'label_upsert', 'chat_label_upsert', 'group_created',
  'outbound_suspended'
]
for (const event of legacyEvents)
  assert.ok(indexSource.includes(`'${event}'`) || indexSource.includes(`"${event}"`), `missing legacy event ${event}`)

console.log(`PASS connector protocol v${PROTOCOL_VERSION}, manifest, capabilities, and legacy RPC/event surface`)
