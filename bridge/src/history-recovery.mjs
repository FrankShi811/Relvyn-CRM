// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

function numericTimestamp(value) {
  const numeric = Number(value?.toString?.() ?? value)
  if (!Number.isFinite(numeric) || numeric <= 0) return 0
  return numeric > 100000000000 ? Math.floor(numeric / 1000) : Math.floor(numeric)
}

function historyRequestTimestampMs(value) {
  const numeric = Number(value?.toString?.() ?? value)
  if (!Number.isFinite(numeric) || numeric <= 0) return 0
  return numeric >= 100000000000 ? Math.floor(numeric) : Math.floor(numeric * 1000)
}

function cursorTimestamp(value) {
  if (value == null || value === '') return 0
  const numeric = Number(value)
  if (Number.isFinite(numeric) && numeric > 0) return numericTimestamp(numeric)
  const parsed = Date.parse(String(value))
  return Number.isFinite(parsed) ? Math.floor(parsed / 1000) : 0
}

function digits(value) {
  return String(value ?? '').replace(/\D/g, '')
}

export function embeddedChatMessages(chat) {
  const messages = []
  for (const item of chat?.messages ?? []) {
    const message = item?.key?.id ? item : item?.message
    if (!message?.key?.id || !message?.key?.remoteJid) continue
    messages.push(message)
  }
  return messages
}

export function latestChatAnchor(chat) {
  return embeddedChatMessages(chat)
    .sort((left, right) => numericTimestamp(right?.messageTimestamp) - numericTimestamp(left?.messageTimestamp))[0] ?? null
}

export function normalizeHistoryCursors(cursors) {
  const byJid = new Map()
  const byPhone = new Map()
  for (const item of Array.isArray(cursors) ? cursors : []) {
    const cursor = {
      jid: String(item?.jid ?? '').trim(),
      phone: digits(item?.phone),
      isGroup: Boolean(item?.isGroup),
      lastMessageAt: cursorTimestamp(item?.lastMessageAt)
    }
    if (cursor.jid) byJid.set(cursor.jid, cursor)
    if (cursor.phone) byPhone.set(cursor.phone, cursor)
  }
  return { byJid, byPhone }
}

export function findHistoryCursor(index, chat) {
  const rawJids = [chat?.id, chat?.jid, chat?.pnJid, chat?.lidJid].map(value => String(value ?? '').trim()).filter(Boolean)
  for (const jid of rawJids) if (index?.byJid?.has(jid)) return index.byJid.get(jid)
  const phone = digits(chat?.phone ?? rawJids.find(jid => jid.endsWith('@s.whatsapp.net')))
  return phone ? index?.byPhone?.get(phone) ?? null : null
}

export function shouldRequestChatHistory(chat, cursor, anchor) {
  if (!anchor?.key?.id || !anchor?.key?.remoteJid) return false
  const unread = Number(chat?.unreadCount ?? 0)
  const anchorTimestamp = numericTimestamp(anchor.messageTimestamp)
  if (!cursor) return unread > 0 || String(anchor.key.remoteJid).endsWith('@g.us')
  if (unread > 0) return true
  return anchorTimestamp > 0 && anchorTimestamp > Number(cursor.lastMessageAt ?? 0) + 1
}

export function historyRequestKey(anchor) {
  return `${String(anchor?.key?.remoteJid ?? '')}|${String(anchor?.key?.id ?? '')}`
}

export function anchorTimestamp(anchor) {
  return historyRequestTimestampMs(anchor?.messageTimestamp)
}
