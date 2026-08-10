// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import {
  isGroupJid,
  isSupportedInboundJid,
  normalizeGroupJid
} from '../src/conversation-routing.mjs'

const checks = [
  [normalizeGroupJid('120363012345678901@g.us') === '120363012345678901@g.us', 'group JID is preserved'],
  [normalizeGroupJid('120363012345678901@G.US') === '120363012345678901@g.us', 'group server is normalized'],
  [normalizeGroupJid('8613012345678@s.whatsapp.net') === '', 'individual JID is not treated as a group'],
  [isGroupJid('120363012345678901@g.us'), 'group JID is recognized'],
  [isSupportedInboundJid('120363012345678901@g.us'), 'groups are supported inbound conversations'],
  [isSupportedInboundJid('8613012345678@s.whatsapp.net'), 'phone JIDs remain supported'],
  [isSupportedInboundJid('123456789@lid'), 'LID conversations remain supported'],
  [!isSupportedInboundJid('status@broadcast'), 'status broadcast remains excluded']
]

const failed = checks.filter(([passed]) => !passed).map(([, label]) => label)
if (failed.length > 0) {
  console.error(`FAIL WhatsApp conversation routing: ${failed.join('; ')}`)
  process.exit(1)
}
console.log('PASS WhatsApp individual/group inbound conversation routing')
