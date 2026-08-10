// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { proto } from '@whiskeysockets/baileys'
import {
  associationEventMatches,
  buildChatLabelPatch,
  buildCustomLabelPatch,
  CUSTOM_LABEL_TYPE,
  labelEventMatches,
  nextCustomLabelId,
  normalizeLabelEvent
} from '../src/label-sync.mjs'

assert.equal(nextCustomLabelId([]), '6')
assert.equal(nextCustomLabelId([{ id: '6' }, { id: '31' }, { id: 'legacy-guid' }]), '32')

const labelPatch = buildCustomLabelPatch({ id: '32', name: 'VIP 客户', color: 4, orderIndex: 2 })
assert.deepEqual(labelPatch.index, ['label_edit', '32'])
assert.equal(labelPatch.syncAction.labelEditAction.type, CUSTOM_LABEL_TYPE)
assert.equal(labelPatch.syncAction.labelEditAction.isActive, true)
assert.equal(labelPatch.syncAction.labelEditAction.isImmutable, false)
assert.equal(labelPatch.syncAction.labelEditAction.orderIndex, 2)
assert.equal(labelPatch.apiVersion, 3)
const encoded = proto.SyncActionData.encode(proto.SyncActionData.fromObject({
  index: Buffer.from(JSON.stringify(labelPatch.index)),
  value: labelPatch.syncAction,
  version: labelPatch.apiVersion
})).finish()
const decodedAction = proto.SyncActionData.decode(encoded).value.labelEditAction
assert.equal(decodedAction.type, CUSTOM_LABEL_TYPE)
assert.equal(decodedAction.orderIndex, 2)
assert.equal(decodedAction.isActive, true)
assert.equal(decodedAction.isImmutable, false)

const chatPatch = buildChatLabelPatch('123456789@lid', '32', true)
assert.deepEqual(chatPatch.index, ['label_jid', '32', '123456789@lid'])
assert.equal(chatPatch.syncAction.labelAssociationAction.labeled, true)

assert.equal(labelEventMatches({ id: '32', name: 'VIP 客户', deleted: false }, { id: '32', name: 'VIP 客户' }), true)
assert.equal(associationEventMatches({ type: 'add', association: { type: 'label_jid', chatId: '123456789:4@lid', labelId: '32' } }, '123456789@lid', '32', true), true)
assert.equal(normalizeLabelEvent({ id: '32', type: CUSTOM_LABEL_TYPE, orderIndex: 2 }).type, CUSTOM_LABEL_TYPE)
assert.throws(() => buildCustomLabelPatch({ id: 'guid', name: 'bad', color: 0 }), /invalid_label_id/)

console.log('PASS modern WhatsApp custom-list label patches and confirmations')
