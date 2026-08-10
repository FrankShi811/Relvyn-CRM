// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { OfflineCatchupCoordinator } from '../src/offline-catchup.mjs'

async function drain(queue) {
  while (queue.length) await queue.shift()()
}

{
  const queue = []
  const status = []
  const presence = []
  const snapshots = []
  const socket = { sendPresenceUpdate: async value => presence.push(value) }
  const coordinator = new OfflineCatchupCoordinator({
    timeoutMs: 1000,
    settleMs: 5,
    enqueue: action => queue.push(action),
    emitStatus: value => status.push(value),
    emitIssue: () => assert.fail('unexpected issue'),
    emitSnapshot: async source => { snapshots.push(source); return { contacts: 4, chats: 3 } },
    getTotals: () => ({ messages: 2, existingSession: true })
  })

  assert.equal(await coordinator.start({ socket, attempt: 7, source: 'startup', existingSession: true }), true)
  assert.deepEqual(presence, ['available'])
  assert.equal(status.at(-1).phase, 'offline_messages')
  assert.equal(coordinator.receivePending({ socket, attempt: 7 }), true)
  await new Promise(resolve => setTimeout(resolve, 15))
  await drain(queue)
  assert.deepEqual(presence, ['available', 'unavailable'])
  assert.deepEqual(snapshots, ['catchup:startup'])
  assert.equal(status.at(-1).pendingNotificationsReceived, true)
  assert.equal(status.at(-1).messages, 2)
  assert.equal(status.at(-1).phase, 'offline_messages_no_new_messages')
  assert.equal(status.at(-1).recoveredMessages, 0)
}

{
  const queue = []
  const status = []
  const presence = []
  let messages = 2
  const socket = { sendPresenceUpdate: async value => presence.push(value) }
  const coordinator = new OfflineCatchupCoordinator({
    timeoutMs: 1000,
    settleMs: 5,
    enqueue: action => queue.push(action),
    emitStatus: value => status.push(value),
    emitIssue: () => assert.fail('unexpected issue'),
    emitSnapshot: async () => ({ contacts: 4, chats: 3 }),
    getTotals: () => ({ messages, existingSession: true })
  })

  await coordinator.start({ socket, attempt: 10, source: 'manual', existingSession: true })
  coordinator.noteHistoryRequest(2)
  messages += 3
  coordinator.noteRecoveredMessages(3)
  assert.equal(coordinator.receivePending({ socket, attempt: 10 }), true)
  await new Promise(resolve => setTimeout(resolve, 15))
  await drain(queue)
  assert.equal(status.at(-1).phase, 'offline_messages')
  assert.equal(status.at(-1).recoveredMessages, 3)
  assert.equal(status.at(-1).requestedChats, 2)
}

{
  const queue = []
  const status = []
  const presence = []
  const socket = { sendPresenceUpdate: async value => presence.push(value) }
  const coordinator = new OfflineCatchupCoordinator({
    timeoutMs: 5,
    enqueue: action => queue.push(action),
    emitStatus: value => status.push(value),
    emitIssue: () => assert.fail('unexpected issue'),
    emitSnapshot: async () => ({ contacts: 0, chats: 0 }),
    getTotals: () => ({ messages: 0, existingSession: true })
  })

  await coordinator.start({ socket, attempt: 8, source: 'manual', existingSession: true })
  await new Promise(resolve => setTimeout(resolve, 15))
  await drain(queue)
  assert.deepEqual(presence, ['available', 'unavailable'])
  assert.equal(status.at(-1).phase, 'offline_messages_timeout')
  assert.equal(status.at(-1).pendingNotificationsReceived, false)
}

{
  const queue = []
  const coordinator = new OfflineCatchupCoordinator({
    timeoutMs: 5,
    enqueue: action => queue.push(action),
    emitStatus: () => {},
    emitIssue: () => {},
    emitSnapshot: async () => assert.fail('cancelled catch-up must not emit a snapshot'),
    getTotals: () => ({})
  })
  const socket = { sendPresenceUpdate: async () => {} }
  await coordinator.start({ socket, attempt: 9, source: 'startup', existingSession: true })
  coordinator.cancel()
  await new Promise(resolve => setTimeout(resolve, 15))
  await drain(queue)
}

console.log('offline-catchup-smoke: ok')
