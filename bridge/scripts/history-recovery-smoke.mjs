// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import {
  anchorTimestamp,
  embeddedChatMessages,
  findHistoryCursor,
  historyRequestKey,
  latestChatAnchor,
  normalizeHistoryCursors,
  shouldRequestChatHistory
} from '../src/history-recovery.mjs'

const newest = { key: { id: 'new', remoteJid: '120363000@g.us' }, messageTimestamp: 200, message: { conversation: 'new' } }
const older = { key: { id: 'old', remoteJid: '120363000@g.us' }, messageTimestamp: 100, message: { conversation: 'old' } }
const chat = { id: '120363000@g.us', unreadCount: 2, messages: [{ message: older }, { message: newest }] }
assert.deepEqual(embeddedChatMessages(chat).map(item => item.key.id), ['old', 'new'])
assert.equal(latestChatAnchor(chat).key.id, 'new')
assert.equal(anchorTimestamp(newest), 200000)
assert.equal(anchorTimestamp({ messageTimestamp: 1720000000 }), 1720000000000)
assert.equal(anchorTimestamp({ messageTimestamp: 1720000000000 }), 1720000000000)
assert.equal(historyRequestKey(newest), '120363000@g.us|new')

const index = normalizeHistoryCursors([
  { jid: '120363000@g.us', lastMessageAt: '1970-01-01T00:02:30Z', isGroup: true },
  { jid: '441234567890@s.whatsapp.net', phone: '+44 123 456 7890', lastMessageAt: 150 }
])
const cursor = findHistoryCursor(index, chat)
assert.equal(cursor.lastMessageAt, 150)
assert.equal(shouldRequestChatHistory(chat, cursor, newest), true, 'unread chat must recover even when cursor matches')
assert.equal(shouldRequestChatHistory({ ...chat, unreadCount: 0 }, cursor, newest), true, 'newer remote anchor must recover')
assert.equal(shouldRequestChatHistory({ ...chat, unreadCount: 0 }, { ...cursor, lastMessageAt: 250 }, newest), false, 'current chat must not refetch')
assert.equal(shouldRequestChatHistory(chat, null, newest), true, 'missing group must recover')

console.log('history-recovery-smoke: ok')
