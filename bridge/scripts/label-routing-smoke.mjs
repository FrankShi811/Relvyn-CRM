// SPDX-License-Identifier: GPL-3.0-only

import { ChatLabelAssociationRouter, normalizeChatLabelAssociation } from '../src/label-routing.mjs'

const phoneAssociation = await normalizeChatLabelAssociation({
  type: 'add',
  association: { type: 'label_jid', chatId: '8613800138000@s.whatsapp.net', labelId: 'priority' }
}, async value => value)

let lidMapping = ''
const router = new ChatLabelAssociationRouter(async value => value.endsWith('@lid') ? lidMapping : value)
const queuedLid = await router.route({
  type: 'remove',
  association: { type: 'label_jid', jid: '123456789:2@lid', labelId: 'follow-up' }
})
const pendingBeforeReplay = router.pendingCount
lidMapping = '447700900123:4@s.whatsapp.net'
const replayedLid = await router.replay('123456789@lid', lidMapping)
const lidAssociation = replayedLid[0]

let lateResolverMapping = ''
const mappingFirstRouter = new ChatLabelAssociationRouter(async value => value.endsWith('@lid') ? lateResolverMapping : value)
await mappingFirstRouter.replay('987654321:9@lid', '15551234567:3@s.whatsapp.net')
const mappedBeforeAssociation = await mappingFirstRouter.route({
  type: 'add',
  association: { type: 'label_jid', chatId: '987654321@lid', labelId: 'mapped-first' }
})
const mappingFirstAssociation = mappedBeforeAssociation[0]

const deviceReplayRouter = new ChatLabelAssociationRouter(async value => value)
await deviceReplayRouter.route({
  type: 'add',
  association: { type: 'label_jid', chatId: '246813579@lid', labelId: 'device-replay' }
})
const deviceQualifiedReplay = await deviceReplayRouter.replay(
  '246813579:7@lid',
  '14155550100@s.whatsapp.net'
)
const deviceReplayAssociation = deviceQualifiedReplay[0]

mappingFirstRouter.clear()

let releaseStaleResolver
const staleResolverResult = new Promise(resolve => { releaseStaleResolver = resolve })
const clearedInFlightRouter = new ChatLabelAssociationRouter(async () => staleResolverResult)
const inFlightAssociation = clearedInFlightRouter.route({
  type: 'add',
  association: { type: 'label_jid', chatId: '1122334455@lid', labelId: 'old-session' }
})
clearedInFlightRouter.clear()
releaseStaleResolver('12025550123@s.whatsapp.net')
const clearedInFlightResult = await inFlightAssociation

const messageLabel = await normalizeChatLabelAssociation({
  type: 'add',
  association: { type: 'label_message', chatId: '8613800138000@s.whatsapp.net', labelId: 'message-only', messageId: 'ABC' }
}, async value => value)

const malformed = await normalizeChatLabelAssociation({
  type: 'unknown',
  association: { type: 'label_jid', chatId: '8613800138000@s.whatsapp.net', labelId: 'new' }
}, async value => value)

const checks = [
  [phoneAssociation?.phone === '8613800138000', 'phone JID remains queryable by bare phone'],
  [phoneAssociation?.chatId === '8613800138000@s.whatsapp.net', 'phone JID remains canonical'],
  [queuedLid.length === 0 && pendingBeforeReplay === 1 && router.pendingCount === 0, 'LID event waits for mapping then leaves no stale queue entry'],
  [lidAssociation?.sourceChatId === '123456789@lid', 'original LID remains available for diagnostics'],
  [lidAssociation?.chatId === '447700900123@s.whatsapp.net', 'device-qualified LID mapping becomes canonical phone JID'],
  [lidAssociation?.type === 'remove', 'remove association semantics are preserved'],
  [mappingFirstAssociation?.chatId === '15551234567@s.whatsapp.net' && mappingFirstRouter.pendingCount === 0, 'mapping received before association remains available'],
  [deviceReplayAssociation?.chatId === '14155550100@s.whatsapp.net' && deviceReplayRouter.pendingCount === 0, 'device-qualified replay matches normalized pending LID'],
  [mappingFirstRouter.mappingCount === 0, 'clear removes session-scoped LID mappings'],
  [clearedInFlightResult.length === 0 && clearedInFlightRouter.pendingCount === 0 && clearedInFlightRouter.mappingCount === 0, 'clear invalidates in-flight work from the previous session'],
  [messageLabel === null, 'message labels never become customer or conversation labels'],
  [malformed === null, 'unknown association action does not cross the label event boundary']
]

const failed = checks.filter(([passed]) => !passed).map(([, label]) => label)
if (failed.length > 0) {
  console.error(`FAIL WhatsApp label routing: ${failed.join('; ')}`)
  process.exit(1)
}

console.log('PASS WhatsApp label association phone/LID routing')
// SPDX-License-Identifier: GPL-3.0-only
