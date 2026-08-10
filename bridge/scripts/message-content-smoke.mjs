// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { isDisplayableMessage, messageContent, messageKind, messageText } from '../src/message-content.mjs'

assert.equal(messageText({ conversation: 'hello, how are you?' }), 'hello, how are you?')
assert.equal(messageText({ ephemeralMessage: { message: { extendedTextMessage: { text: "what's up bro?" } } } }), "what's up bro?")
assert.equal(messageText({ editedMessage: { message: { conversation: 'edited text' } } }), 'edited text')
assert.equal(messageText({ deviceSentMessage: { message: { conversation: 'sent from phone' } } }), 'sent from phone')
assert.equal(messageKind({ imageMessage: { caption: 'catalog photo' } }), 'image')
assert.equal(messageText({ imageMessage: { caption: 'catalog photo' } }), 'catalog photo')
assert.equal(messageKind({ protocolMessage: {} }), 'control')
assert.equal(isDisplayableMessage({ protocolMessage: {} }), false)
assert.equal(isDisplayableMessage({ conversation: '' }), false)
assert.equal(messageContent(null), null)

console.log('PASS  WhatsApp message content normalization and control-message filtering')
