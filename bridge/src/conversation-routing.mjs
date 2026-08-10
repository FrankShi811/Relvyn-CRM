// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

export function normalizeGroupJid(value) {
  const candidate = String(value ?? '').trim()
  if (!candidate || /\s/.test(candidate)) return ''
  const separator = candidate.lastIndexOf('@')
  if (separator <= 0 || candidate.slice(separator + 1).toLowerCase() !== 'g.us') return ''
  return `${candidate.slice(0, separator)}@g.us`
}

export function isGroupJid(value) {
  return Boolean(normalizeGroupJid(value))
}

export function isSupportedInboundJid(value) {
  const candidate = String(value ?? '').trim().toLowerCase()
  return isGroupJid(candidate)
    || candidate.endsWith('@s.whatsapp.net')
    || candidate.endsWith('@lid')
}
