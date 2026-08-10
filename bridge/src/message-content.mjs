// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { normalizeMessageContent } from '@whiskeysockets/baileys'

const mediaKinds = new Map([
  ['imageMessage', 'image'],
  ['videoMessage', 'video'],
  ['audioMessage', 'audio'],
  ['documentMessage', 'document'],
  ['stickerMessage', 'sticker']
])

export function messageContent(message) {
  let current = message
  for (let depth = 0; depth < 8; depth++) {
    if (!current) return null
    const deviceMessage = current.deviceSentMessage?.message
    if (deviceMessage) {
      current = deviceMessage
      continue
    }
    const normalized = normalizeMessageContent(current)
    if (!normalized) return null
    const statusMessage = normalized.statusMentionMessage?.message
      ?? normalized.groupStatusMentionMessage?.message
    if (statusMessage) {
      current = statusMessage
      continue
    }
    return normalized
  }
  return normalizeMessageContent(current) ?? current ?? null
}

export function messageText(message) {
  const content = messageContent(message)
  if (!content) return ''
  return content.conversation
    ?? content.extendedTextMessage?.text
    ?? content.imageMessage?.caption
    ?? content.videoMessage?.caption
    ?? content.documentMessage?.caption
    ?? content.buttonsResponseMessage?.selectedDisplayText
    ?? content.listResponseMessage?.title
    ?? content.templateButtonReplyMessage?.selectedDisplayText
    ?? content.reactionMessage?.text
    ?? content.contactMessage?.displayName
    ?? content.contactsArrayMessage?.displayName
    ?? content.locationMessage?.name
    ?? content.locationMessage?.address
    ?? content.liveLocationMessage?.caption
    ?? content.pollCreationMessage?.name
    ?? content.pollCreationMessageV2?.name
    ?? content.pollCreationMessageV3?.name
    ?? content.pollCreationMessageV4?.name
    ?? content.pollCreationMessageV5?.name
    ?? content.eventMessage?.name
    ?? ''
}

export function messageKind(message) {
  const content = messageContent(message)
  if (!content) return 'unknown'
  for (const [field, kind] of mediaKinds) if (content[field]) return kind
  if (content.contactMessage || content.contactsArrayMessage) return 'contact'
  if (content.locationMessage || content.liveLocationMessage) return 'location'
  if (content.pollCreationMessage || content.pollCreationMessageV2 || content.pollCreationMessageV3
      || content.pollCreationMessageV4 || content.pollCreationMessageV5 || content.pollUpdateMessage) return 'poll'
  if (content.reactionMessage) return 'reaction'
  if (content.eventMessage) return 'event'
  if (content.protocolMessage || content.senderKeyDistributionMessage || content.appStateSyncKeyShare
      || content.historySyncNotification || content.keepInChatMessage) return 'control'
  if (content.conversation !== undefined || content.extendedTextMessage || content.buttonsResponseMessage
      || content.listResponseMessage || content.templateButtonReplyMessage) return 'text'
  return 'unsupported'
}

export function isDisplayableMessage(message) {
  const kind = messageKind(message)
  if (kind === 'unknown' || kind === 'control') return false
  return kind !== 'text' || Boolean(messageText(message).trim())
}
