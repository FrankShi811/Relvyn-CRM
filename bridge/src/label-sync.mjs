// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

const MAX_LABEL_ID = 2_147_483_647
const FIRST_CUSTOM_LABEL_ID = 6

export const CUSTOM_LABEL_TYPE = 5

export function nextCustomLabelId(labels) {
  let highest = FIRST_CUSTOM_LABEL_ID - 1
  for (const label of labels ?? []) {
    const rawId = String(label?.id ?? '').trim()
    if (!/^\d+$/.test(rawId)) continue
    const candidate = Number(rawId)
    if (Number.isSafeInteger(candidate) && candidate >= 0 && candidate <= MAX_LABEL_ID) {
      highest = Math.max(highest, candidate)
    }
  }
  if (highest >= MAX_LABEL_ID) throw new Error('whatsapp_label_id_exhausted')
  return String(highest + 1)
}

export function buildCustomLabelPatch({ id, name, color, deleted = false, orderIndex = 0 }) {
  const normalizedId = requireNumericLabelId(id)
  const normalizedName = String(name ?? '').trim()
  if (!normalizedName || normalizedName.length > 100) throw new Error('invalid_label_name')
  const normalizedColor = Number(color)
  if (!Number.isInteger(normalizedColor) || normalizedColor < 0 || normalizedColor > 19) {
    throw new Error('invalid_label_color')
  }
  const normalizedOrder = Number.isInteger(Number(orderIndex))
    ? Math.max(0, Math.min(MAX_LABEL_ID, Number(orderIndex)))
    : 0
  const removed = Boolean(deleted)
  return {
    syncAction: {
      labelEditAction: {
        name: normalizedName,
        color: normalizedColor,
        deleted: removed,
        orderIndex: normalizedOrder,
        isActive: !removed,
        type: CUSTOM_LABEL_TYPE,
        isImmutable: false,
        muteEndTimeMs: 0
      }
    },
    index: ['label_edit', normalizedId],
    type: 'regular',
    apiVersion: 3
  }
}

export function buildChatLabelPatch(jid, labelId, add) {
  const normalizedJid = String(jid ?? '').trim()
  if (!/^[^@]+@(s\.whatsapp\.net|lid)$/.test(normalizedJid)) throw new Error('invalid_label_chat_jid')
  return {
    syncAction: { labelAssociationAction: { labeled: Boolean(add) } },
    index: ['label_jid', requireNumericLabelId(labelId), normalizedJid],
    type: 'regular',
    apiVersion: 3
  }
}

export function normalizeLabelEvent(label) {
  if (!label || typeof label !== 'object') return null
  const id = String(label.id ?? '').trim()
  if (!id) return null
  return {
    id,
    name: String(label.name ?? ''),
    color: Number(label.color ?? 0),
    deleted: Boolean(label.deleted),
    predefinedId: label.predefinedId != null ? Number(label.predefinedId) : null,
    orderIndex: label.orderIndex != null ? Number(label.orderIndex) : null,
    isActive: label.isActive != null ? Boolean(label.isActive) : null,
    type: label.type != null ? Number(label.type) : null,
    isImmutable: label.isImmutable != null ? Boolean(label.isImmutable) : null
  }
}

export function labelEventMatches(actual, expected) {
  const label = normalizeLabelEvent(actual)
  return Boolean(label)
    && label.id === String(expected?.id ?? '')
    && label.name === String(expected?.name ?? '')
    && label.deleted === Boolean(expected?.deleted)
}

export function associationEventMatches(payload, expectedJid, expectedLabelId, expectedAdd) {
  if (!payload || typeof payload !== 'object') return false
  const association = payload.association
  return association?.type === 'label_jid'
    && normalizeDeviceJid(association.chatId) === normalizeDeviceJid(expectedJid)
    && String(association.labelId ?? '') === String(expectedLabelId ?? '')
    && (payload.type === 'add') === Boolean(expectedAdd)
}

function requireNumericLabelId(value) {
  const normalized = String(value ?? '').trim()
  const numeric = Number(normalized)
  if (!/^\d+$/.test(normalized) || !Number.isSafeInteger(numeric) || numeric < 0 || numeric > MAX_LABEL_ID) {
    throw new Error('invalid_label_id')
  }
  return normalized
}

function normalizeDeviceJid(value) {
  const candidate = String(value ?? '').trim().toLowerCase()
  const match = /^(\d+)(?::\d+)?@(s\.whatsapp\.net|lid)$/.exec(candidate)
  return match ? `${match[1]}@${match[2]}` : candidate
}
