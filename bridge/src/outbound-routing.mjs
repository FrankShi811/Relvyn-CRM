// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { jidDecode, jidEncode, jidNormalizedUser } from '@whiskeysockets/baileys'

const supportedDirectServers = new Set(['s.whatsapp.net', 'lid'])

export function normalizeOutboundUserJid(value) {
  const candidate = String(value ?? '').trim()
  if (!candidate) return ''
  const normalized = jidNormalizedUser(candidate)
  const decoded = jidDecode(normalized)
  if (!decoded?.user || !supportedDirectServers.has(decoded.server)) return ''
  return jidEncode(decoded.user, decoded.server)
}

export function summarizeOutboundDeviceFanout(devices, senderJids, recipientJid) {
  const senderUsers = new Set(
    (senderJids ?? [])
      .map(normalizeOutboundUserJid)
      .map(jid => jidDecode(jid)?.user)
      .filter(Boolean)
  )
  const recipientUser = jidDecode(normalizeOutboundUserJid(recipientJid))?.user ?? ''
  const senderDevices = new Set()
  const recipientDevices = new Set()

  for (const device of devices ?? []) {
    const rawJid = String(device?.jid ?? '').trim()
      || jidEncode(device?.user ?? '', device?.server ?? 's.whatsapp.net', device?.device)
    const decoded = jidDecode(rawJid)
    if (!decoded?.user) continue
    if (senderUsers.has(decoded.user)) senderDevices.add(rawJid)
    else if (recipientUser && decoded.user === recipientUser) recipientDevices.add(rawJid)
  }

  return {
    senderDeviceCount: senderDevices.size,
    recipientDeviceCount: recipientDevices.size
  }
}
