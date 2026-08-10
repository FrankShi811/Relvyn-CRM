// SPDX-License-Identifier: GPL-3.0-only

import { normalizeOutboundUserJid } from './outbound-routing.mjs'

function parseChatLabelAssociation(payload) {
  const association = payload?.association
  if (!association || association.type !== 'label_jid') return null
  if (payload.type !== 'add' && payload.type !== 'remove') return null

  const rawSourceChatId = String(association.chatId ?? association.jid ?? '').trim()
  const sourceChatId = normalizeOutboundUserJid(rawSourceChatId) || rawSourceChatId
  const labelId = String(association.labelId ?? '').trim()
  if (!sourceChatId || !labelId) return null
  return { sourceChatId, labelId, type: payload.type }
}

export async function normalizeChatLabelAssociation(payload, resolvePhoneJid) {
  const parsed = parseChatLabelAssociation(payload)
  if (!parsed || typeof resolvePhoneJid !== 'function') return null

  const phoneJid = normalizeOutboundUserJid(await resolvePhoneJid(parsed.sourceChatId))
  if (!phoneJid.endsWith('@s.whatsapp.net')) return null

  const phone = phoneJid.slice(0, -'@s.whatsapp.net'.length).replace(/\D/g, '')
  if (phone.length < 8 || phone.length > 15) return null

  return {
    chatId: `${phone}@s.whatsapp.net`,
    sourceChatId: parsed.sourceChatId,
    labelId: parsed.labelId,
    type: parsed.type,
    phone
  }
}

export class ChatLabelAssociationRouter {
  constructor(resolvePhoneJid, { maxPending = 500, maxAgeMs = 15 * 60 * 1000 } = {}) {
    if (typeof resolvePhoneJid !== 'function') throw new TypeError('resolvePhoneJid must be a function')
    this.resolvePhoneJid = resolvePhoneJid
    this.maxPending = Math.max(1, Number(maxPending) || 500)
    this.maxAgeMs = Math.max(1000, Number(maxAgeMs) || 15 * 60 * 1000)
    this.pending = []
    this.lidMappings = new Map()
    this.epoch = 0
  }

  get pendingCount() { return this.pending.length }
  get mappingCount() { return this.lidMappings.size }

  clear() {
    this.epoch += 1
    this.pending = []
    this.lidMappings.clear()
  }

  rememberMapping(sourceChatId, resolvedPhoneJid, epoch = this.epoch) {
    if (epoch !== this.epoch) return ''
    const source = normalizeOutboundUserJid(sourceChatId)
    const phoneJid = normalizeOutboundUserJid(resolvedPhoneJid)
    if (!source.endsWith('@lid') || !phoneJid.endsWith('@s.whatsapp.net')) return ''
    this.lidMappings.set(source, phoneJid)
    return phoneJid
  }

  async resolveWithCache(candidate, epoch = this.epoch) {
    if (epoch !== this.epoch) return ''
    const source = normalizeOutboundUserJid(candidate)
    const cached = this.lidMappings.get(source)
    if (cached) return cached

    const resolved = normalizeOutboundUserJid(await this.resolvePhoneJid(source || candidate))
    if (epoch !== this.epoch) return ''
    if (source.endsWith('@lid') && resolved.endsWith('@s.whatsapp.net')) {
      this.lidMappings.set(source, resolved)
    }
    return resolved
  }

  async route(payload, now = Date.now()) {
    const epoch = this.epoch
    const parsed = parseChatLabelAssociation(payload)
    if (!parsed) return []

    const normalized = await normalizeChatLabelAssociation(payload, candidate => this.resolveWithCache(candidate, epoch))
    if (epoch !== this.epoch) return []
    if (normalized) return [normalized]
    if (!parsed.sourceChatId.toLowerCase().endsWith('@lid')) return []

    this.prune(now)
    const previous = this.pending.at(-1)
    if (!previous
        || previous.parsed.sourceChatId !== parsed.sourceChatId
        || previous.parsed.labelId !== parsed.labelId
        || previous.parsed.type !== parsed.type) {
      this.pending.push({
        payload: {
          type: parsed.type,
          association: { type: 'label_jid', chatId: parsed.sourceChatId, labelId: parsed.labelId }
        },
        parsed,
        queuedAt: now
      })
      while (this.pending.length > this.maxPending) this.pending.shift()
    }
    return []
  }

  async replay(sourceChatId, resolvedPhoneJid = '', now = Date.now()) {
    const epoch = this.epoch
    const rawSource = String(sourceChatId ?? '').trim()
    const source = normalizeOutboundUserJid(rawSource) || rawSource
    if (!source) return []
    this.prune(now)
    const explicitPhoneJid = this.rememberMapping(source, resolvedPhoneJid, epoch)
    const resolver = explicitPhoneJid.endsWith('@s.whatsapp.net')
      ? async candidate => normalizeOutboundUserJid(candidate) === source
        ? explicitPhoneJid
        : this.resolveWithCache(candidate, epoch)
      : candidate => this.resolveWithCache(candidate, epoch)

    const replayed = []
    const remaining = []
    for (const entry of this.pending) {
      if (entry.parsed.sourceChatId !== source) {
        remaining.push(entry)
        continue
      }
      const normalized = await normalizeChatLabelAssociation(entry.payload, resolver)
      if (epoch !== this.epoch) return []
      if (normalized) replayed.push(normalized)
      else remaining.push(entry)
    }
    if (epoch !== this.epoch) return []
    this.pending = remaining
    return replayed
  }

  prune(now = Date.now()) {
    const threshold = now - this.maxAgeMs
    this.pending = this.pending.filter(entry => entry.queuedAt >= threshold)
  }
}
// SPDX-License-Identifier: GPL-3.0-only
