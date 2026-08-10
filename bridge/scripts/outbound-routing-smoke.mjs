// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { normalizeOutboundUserJid, summarizeOutboundDeviceFanout } from '../src/outbound-routing.mjs'

assert.equal(normalizeOutboundUserJid('447700900123:0@s.whatsapp.net'), '447700900123@s.whatsapp.net')
assert.equal(normalizeOutboundUserJid('447700900123:37@s.whatsapp.net'), '447700900123@s.whatsapp.net')
assert.equal(normalizeOutboundUserJid('447700900123@c.us'), '447700900123@s.whatsapp.net')
assert.equal(normalizeOutboundUserJid('123456789:0@lid'), '123456789@lid')
assert.equal(normalizeOutboundUserJid('120363000000000000@g.us'), '')
assert.equal(normalizeOutboundUserJid('not-a-jid'), '')

const fanout = summarizeOutboundDeviceFanout([
  { jid: '8613073611720:0@s.whatsapp.net', user: '8613073611720', device: 0, server: 's.whatsapp.net' },
  { jid: '8613073611720:4@s.whatsapp.net', user: '8613073611720', device: 4, server: 's.whatsapp.net' },
  { jid: '13373224256:0@s.whatsapp.net', user: '13373224256', device: 0, server: 's.whatsapp.net' },
  { jid: '13373224256:3@s.whatsapp.net', user: '13373224256', device: 3, server: 's.whatsapp.net' },
  { jid: '13373224256:3@s.whatsapp.net', user: '13373224256', device: 3, server: 's.whatsapp.net' }
], ['8613073611720:12@s.whatsapp.net'], '13373224256:0@s.whatsapp.net')

assert.deepEqual(fanout, { senderDeviceCount: 2, recipientDeviceCount: 2 })

console.log('PASS  WhatsApp outbound bare-JID normalization and sender/recipient device fanout')
