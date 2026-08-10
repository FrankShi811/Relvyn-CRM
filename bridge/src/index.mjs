// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import readline from 'node:readline'
import path from 'node:path'
import fs from 'node:fs/promises'
import crypto from 'node:crypto'
import process from 'node:process'
import QRCode from 'qrcode'
import pino from 'pino'
import makeWASocket, {
  ALL_WA_PATCH_NAMES,
  Browsers,
  BufferJSON,
  DisconnectReason,
  downloadMediaMessage,
  fetchLatestBaileysVersion,
  fetchLatestWaWebVersion,
  initAuthCreds,
  proto
} from '@whiskeysockets/baileys'
import { isDisplayableMessage, messageContent, messageKind, messageText } from './message-content.mjs'
import { normalizeOutboundUserJid, summarizeOutboundDeviceFanout } from './outbound-routing.mjs'
import { isGroupJid, isSupportedInboundJid, normalizeGroupJid } from './conversation-routing.mjs'
import { resolveBaileysVersion } from './connection-bootstrap.mjs'
import { createProxyAgent, normalizeProxyUrl, safeProxyLabel } from './network-routing.mjs'
import { OfflineCatchupCoordinator } from './offline-catchup.mjs'
import {
  anchorTimestamp,
  embeddedChatMessages,
  findHistoryCursor,
  historyRequestKey,
  latestChatAnchor,
  normalizeHistoryCursors,
  shouldRequestChatHistory
} from './history-recovery.mjs'

const logger = pino({ level: 'silent' })
const QR_GENERATION_WATCHDOG_MS = 35000
const DESKTOP_UPGRADE_MAX_FAILURES = 1
const VERSION_LOOKUP_TIMEOUT_MS = 3000
const DESKTOP_HISTORY_PROFILE_FILE = 'desktop-history-profile.json'
const PROTOCOL_VERSION_CACHE_FILE = 'whatsapp-protocol-version.json'
const state = {
  accountId: 'default',
  sessionDir: '',
  socket: null,
  connection: 'idle',
  reconnectTimer: null,
  pairingTimer: null,
  connectionAttempt: 0,
  qrSeen: false,
  manualDisconnect: false,
  proxyUrl: '',
  currentProxyUrl: '',
  proxySource: '',
  allowDirectFallback: false,
  directFallbackUsed: false,
  immediateReconnect: false,
  connectionGeneration: 0,
  lifecycleQueue: Promise.resolve(),
  authKey: null,
  existingSession: false,
  desktopHistoryProfile: false,
  newDesktopPairing: false,
  desktopUpgradeRequested: false,
  desktopUpgradeFailures: 0,
  pendingNotificationsAttempt: 0,
  contacts: new Map(),
  chats: new Map(),
  messages: new Map(),
  outboundTargets: new Map(),
  mediaDownloads: new Map(),
  historyTotals: { contacts: 0, chats: 0, messages: 0 },
  historyRecovery: {
    active: false,
    cursorIndex: normalizeHistoryCursors([]),
    requested: new Set(),
    source: 'startup'
  },
  syncQueue: Promise.resolve()
}

const authFileLocks = new Map()

function desktopHistoryProfilePath() {
  return path.join(state.sessionDir, DESKTOP_HISTORY_PROFILE_FILE)
}

async function hasDesktopHistoryProfile() {
  try {
    const content = await fs.readFile(desktopHistoryProfilePath(), 'utf8')
    return JSON.parse(content)?.mode === 'desktop_full_history'
  } catch (error) {
    if (error?.code === 'ENOENT' || error instanceof SyntaxError) return false
    throw error
  }
}

async function writeDesktopHistoryProfile() {
  const target = desktopHistoryProfilePath()
  const temporary = `${target}.${process.pid}.${Date.now()}.tmp`
  await fs.writeFile(temporary, JSON.stringify({ mode: 'desktop_full_history', version: 1 }), 'utf8')
  await fs.rename(temporary, target)
  state.desktopHistoryProfile = true
  state.newDesktopPairing = false
}

function emitDesktopHistoryRepairRequired(source = 'manual') {
  const message = '当前账号以网页模式连接，正在尽力补齐可用的历史消息；个别较早的会话消息可能因 WhatsApp 加密限制无法完整恢复，最新消息收发不受影响。'
  emit({
    type: 'event', event: 'sync_status', accountId: state.accountId,
    data: {
      state: 'action_required', phase: 'offline_history_profile', progress: null,
      source, error: message, existingSession: true, requiresQr: true
    }
  })
}

function parseEncryptionKey(value) {
  const key = Buffer.from(String(value ?? ''), 'base64')
  if (key.length !== 32) throw new Error('invalid_session_encryption_key')
  return key
}

function fixAuthFileName(file) {
  return String(file ?? '').replace(/\//g, '__').replace(/:/g, '-')
}

async function withAuthFileLock(file, action) {
  const previous = authFileLocks.get(file) ?? Promise.resolve()
  let release
  const next = new Promise(resolve => { release = resolve })
  const tail = previous.then(() => next)
  authFileLocks.set(file, tail)
  await previous
  try { return await action() }
  finally {
    release()
    if (authFileLocks.get(file) === tail) authFileLocks.delete(file)
  }
}

function encryptAuthData(data, key) {
  const iv = crypto.randomBytes(12)
  const cipher = crypto.createCipheriv('aes-256-gcm', key, iv)
  const plaintext = Buffer.from(JSON.stringify(data, BufferJSON.replacer), 'utf8')
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()])
  return JSON.stringify({ version: 1, algorithm: 'aes-256-gcm', iv: iv.toString('base64'), tag: cipher.getAuthTag().toString('base64'), data: ciphertext.toString('base64') })
}

function decryptAuthData(envelope, key) {
  const parsed = JSON.parse(envelope)
  if (parsed?.version !== 1 || parsed?.algorithm !== 'aes-256-gcm') throw new Error('unsupported_auth_state_format')
  const decipher = crypto.createDecipheriv('aes-256-gcm', key, Buffer.from(parsed.iv, 'base64'))
  decipher.setAuthTag(Buffer.from(parsed.tag, 'base64'))
  const plaintext = Buffer.concat([decipher.update(Buffer.from(parsed.data, 'base64')), decipher.final()]).toString('utf8')
  return JSON.parse(plaintext, BufferJSON.reviver)
}

async function useEncryptedAuthState(folder, key) {
  await fs.mkdir(folder, { recursive: true })

  const writeDataUnlocked = async (data, file) => {
    const encryptedPath = path.join(folder, `${fixAuthFileName(file)}.enc`)
    const temporaryPath = `${encryptedPath}.${process.pid}.tmp`
    await fs.writeFile(temporaryPath, encryptAuthData(data, key), { encoding: 'utf8', mode: 0o600 })
    await fs.rm(encryptedPath, { force: true })
    await fs.rename(temporaryPath, encryptedPath)
  }
  const writeData = async (data, file) => withAuthFileLock(file, () => writeDataUnlocked(data, file))

  const readData = async file => withAuthFileLock(file, async () => {
    const encryptedPath = path.join(folder, `${fixAuthFileName(file)}.enc`)
    try {
      return decryptAuthData(await fs.readFile(encryptedPath, 'utf8'), key)
    } catch (error) {
      if (error?.code !== 'ENOENT') throw new Error(`auth_state_decrypt_failed:${fixAuthFileName(file)}`)
    }

    const legacyPath = path.join(folder, fixAuthFileName(file))
    try {
      const legacy = JSON.parse(await fs.readFile(legacyPath, 'utf8'), BufferJSON.reviver)
      await writeDataUnlocked(legacy, file)
      await fs.rm(legacyPath, { force: true })
      return legacy
    } catch (error) {
      if (error?.code === 'ENOENT') return null
      throw new Error(`legacy_auth_state_migration_failed:${fixAuthFileName(file)}`)
    }
  })

  const removeData = async file => withAuthFileLock(file, async () => {
    await Promise.all([
      fs.rm(path.join(folder, `${fixAuthFileName(file)}.enc`), { force: true }),
      fs.rm(path.join(folder, fixAuthFileName(file)), { force: true })
    ])
  })

  const storedCreds = await readData('creds.json')
  const creds = storedCreds || initAuthCreds()
  if (!storedCreds) await writeData(creds, 'creds.json')
  return {
    state: {
      creds,
      keys: {
        get: async (type, ids) => {
          const data = {}
          await Promise.all(ids.map(async id => {
            let value = await readData(`${type}-${id}.json`)
            if (type === 'app-state-sync-key' && value) value = proto.Message.AppStateSyncKeyData.create(value)
            data[id] = value
          }))
          return data
        },
        set: async data => {
          const tasks = []
          for (const category in data) for (const id in data[category]) {
            const value = data[category][id]
            const file = `${category}-${id}.json`
            tasks.push(value ? writeData(value, file) : removeData(file))
          }
          await Promise.all(tasks)
        }
      }
    },
    saveCreds: async () => writeData(creds, 'creds.json')
  }
}

function isUnreadableAuthState(error) {
  const message = error instanceof Error ? error.message : String(error ?? '')
  return message.startsWith('auth_state_decrypt_failed:') || message.startsWith('legacy_auth_state_migration_failed:')
}

async function recoverUnreadableAuthState(error) {
  const reason = safeError(error)
  const suffix = new Date().toISOString().replace(/[:.]/g, '-').replace('T', '_').replace('Z', '')
  const backupDir = `${state.sessionDir}.unreadable-${suffix}`
  await fs.rename(state.sessionDir, backupDir)
  await fs.mkdir(state.sessionDir, { recursive: true })
  state.existingSession = false
  const data = {
    reason: 'local_session_unreadable',
    detail: reason,
    backupName: path.basename(backupDir),
    requiresQr: true
  }
  emit({ type: 'event', event: 'auth_recovery', accountId: state.accountId, data })
  return data
}

async function loadAuthStateWithRecovery() {
  try {
    return { ...(await useEncryptedAuthState(state.sessionDir, state.authKey)), recovered: false, recovery: null }
  } catch (error) {
    if (!isUnreadableAuthState(error)) throw error
    const recovery = await recoverUnreadableAuthState(error)
    return { ...(await useEncryptedAuthState(state.sessionDir, state.authKey)), recovered: true, recovery }
  }
}

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`)
}

function reply(requestId, ok, result = null, error = null) {
  emit({ type: 'response', requestId, ok, result, error })
}

function safeError(error) {
  const message = error instanceof Error ? error.message : String(error ?? 'unknown_error')
  return message.replace(/Bearer\s+[^\s]+/gi, 'Bearer [REDACTED]').slice(0, 1000)
}

function enqueueSync(action, phase) {
  state.syncQueue = state.syncQueue
    .then(action)
    .catch(error => emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'failed', phase, error: safeError(error) } }))
  return state.syncQueue
}

function validateAccountId(value) {
  const normalized = String(value ?? '').trim()
  if (!/^[a-zA-Z0-9_-]{1,64}$/.test(normalized)) throw new Error('invalid_account_id')
  return normalized
}

function resolveSessionDir(accountId) {
  return path.join(resolveDataRoot(), 'whatsapp-sessions', accountId)
}

function resolveDataRoot() {
  const configured = String(process.env.WAFLOW_DATA_ROOT ?? '').trim()
  if (configured) return path.resolve(configured)
  const localAppData = process.env.LOCALAPPDATA
  if (!localAppData) throw new Error('LOCALAPPDATA_not_available')
  return path.join(localAppData, 'WAFlow')
}

async function readCachedProtocolVersion() {
  try {
    const raw = JSON.parse(await fs.readFile(path.join(resolveDataRoot(), PROTOCOL_VERSION_CACHE_FILE), 'utf8'))
    const version = raw?.version
    return Array.isArray(version)
      && version.length === 3
      && version.every(part => Number.isInteger(part) && part >= 0)
      ? version
      : null
  } catch {
    return null
  }
}

async function cacheProtocolVersion(version, source) {
  if (!Array.isArray(version) || version.length !== 3) return
  const cachePath = path.join(resolveDataRoot(), PROTOCOL_VERSION_CACHE_FILE)
  const temporaryPath = `${cachePath}.${process.pid}.tmp`
  await fs.mkdir(path.dirname(cachePath), { recursive: true })
  await fs.writeFile(temporaryPath, JSON.stringify({ version, source, updatedAt: new Date().toISOString() }), 'utf8')
  await fs.rename(temporaryPath, cachePath)
}

function jidFromPhone(phone) {
  const digits = String(phone ?? '').replace(/\D/g, '')
  if (digits.length < 8 || digits.length > 15) throw new Error('invalid_whatsapp_number')
  return `${digits}@s.whatsapp.net`
}

async function resolveOutboundJid(phone, explicitJid = '') {
  const digits = String(phone ?? '').replace(/\D/g, '')
  const fallback = jidFromPhone(digits)
  const explicit = await verifiedPhoneJid(explicitJid, digits)
  if (explicit) return explicit

  const cached = [...state.contacts.values(), ...state.chats.values()]
    .find(item => item?.phone === digits || phoneFromJid(item?.jid) === digits || phoneFromJid(item?.sourceJid) === digits)
  if (cached) {
    const resolved = await resolveUserJid(cached.jid, cached.sourceJid)
    const verified = await verifiedPhoneJid(resolved, digits)
    if (verified) return verified
  }

  try {
    const matches = await state.socket?.onWhatsApp(digits)
    for (const match of matches ?? []) {
      if (!match?.exists) continue
      const verified = await verifiedPhoneJid(match?.jid, digits)
      if (verified) return verified
    }
  } catch {
    // Number discovery is advisory only. A temporary discovery failure must not block a real send attempt.
  }
  return fallback
}

function timestampToIso(value) {
  if (value == null) return new Date().toISOString()
  const seconds = typeof value === 'number' ? value : Number(value?.toString?.() ?? value)
  if (!Number.isFinite(seconds)) return new Date().toISOString()
  return new Date(Math.abs(seconds) >= 1_000_000_000_000 ? seconds : seconds * 1000).toISOString()
}

function phoneFromJid(jid) {
  const normalized = normalizeOutboundUserJid(jid)
  return normalized.endsWith('@s.whatsapp.net') ? normalized.split('@')[0].replace(/\D/g, '') : ''
}

async function phoneJidFromAnyJid(jid) {
  const candidate = normalizeOutboundUserJid(jid)
  if (candidate.endsWith('@s.whatsapp.net')) return candidate
  if (!candidate.endsWith('@lid')) return ''
  try {
    const mapped = await state.socket?.signalRepository?.lidMapping?.getPNForLID(candidate)
    const normalized = normalizeOutboundUserJid(mapped)
    return normalized.endsWith('@s.whatsapp.net') ? normalized : ''
  } catch {
    return ''
  }
}

async function verifiedPhoneJid(jid, expectedPhone) {
  const phoneJid = await phoneJidFromAnyJid(jid)
  return phoneFromJid(phoneJid) === String(expectedPhone ?? '').replace(/\D/g, '') ? phoneJid : ''
}

async function prepareOutboundDeviceFanout(jid) {
  const targetJid = normalizeOutboundUserJid(jid)
  if (!targetJid) throw new Error('whatsapp_target_jid_invalid')
  const socket = state.socket
  if (!socket || typeof socket.getUSyncDevices !== 'function') {
    throw new Error('whatsapp_sender_device_sync_unavailable:当前 WhatsApp 连接不支持发送者设备同步，消息尚未发送。请重新连接后再试。')
  }

  const senderPhoneJid = normalizeOutboundUserJid(socket.user?.id)
  const senderLid = normalizeOutboundUserJid(socket.user?.lid)
  const senderIdentity = targetJid.endsWith('@lid') ? (senderLid || senderPhoneJid) : senderPhoneJid
  if (!senderIdentity) {
    throw new Error('whatsapp_sender_identity_missing:未取得当前 WhatsApp 账号身份，消息尚未发送。请重新连接后再试。')
  }

  // Force a fresh USync lookup before every outbound message. Baileys then uses the
  // refreshed internal cache to encrypt the normal message for the customer and a
  // deviceSentMessage copy for every other device belonging to the sender.
  const devices = await socket.getUSyncDevices([senderIdentity, targetJid], false, false)
  const fanout = summarizeOutboundDeviceFanout(devices, [senderPhoneJid, senderLid], targetJid)
  if (fanout.senderDeviceCount < 1) {
    throw new Error('whatsapp_sender_device_sync_unavailable:未发现可接收消息副本的发送者手机设备，消息尚未发送。请确认手机 WhatsApp 在线后重新连接账号。')
  }
  if (fanout.recipientDeviceCount < 1) {
    throw new Error('whatsapp_recipient_devices_unavailable:未发现客户的可用 WhatsApp 设备，消息尚未发送。请稍后再试。')
  }
  return { jid: targetJid, senderDeviceSyncPrepared: true, ...fanout }
}

function rememberOutboundTarget(providerMessageId, target) {
  const id = String(providerMessageId ?? '').trim()
  if (!id) throw new Error('whatsapp_server_message_id_missing')
  state.outboundTargets.set(id, target)
  while (state.outboundTargets.size > 5000) {
    const oldest = state.outboundTargets.keys().next().value
    if (!oldest) break
    state.outboundTargets.delete(oldest)
  }
}

async function requireVerifiedOutboundResult(result, requestedPhone, requestedJid) {
  const providerMessageId = String(result?.key?.id ?? '').trim()
  if (!providerMessageId) throw new Error('whatsapp_server_message_id_missing')
  const expectedPhone = String(requestedPhone ?? '').replace(/\D/g, '')
  if (!expectedPhone) throw new Error('invalid_whatsapp_number')

  const actualCandidates = [
    String(result?.key?.remoteJidAlt ?? '').trim(),
    String(result?.key?.remoteJid ?? '').trim()
  ].filter(Boolean)
  let actualPhoneJid = ''
  for (const candidate of actualCandidates) {
    actualPhoneJid = await verifiedPhoneJid(candidate, expectedPhone)
    if (actualPhoneJid) break
  }
  if (!actualPhoneJid) throw new Error('whatsapp_target_not_verified')

  const requestedPhoneJid = await verifiedPhoneJid(requestedJid, expectedPhone)
  if (!requestedPhoneJid) throw new Error('whatsapp_target_not_verified')
  const target = {
    requestedPhone: expectedPhone,
    requestedJid: requestedPhoneJid,
    remoteJid: String(result?.key?.remoteJid ?? ''),
    remoteJidAlt: String(result?.key?.remoteJidAlt ?? ''),
    createdAt: new Date().toISOString()
  }
  rememberOutboundTarget(providerMessageId, target)
  return { providerMessageId, ...target, targetVerified: true }
}

async function receiptBelongsToPhone(receipt, expectedPhone) {
  const userJid = String(receipt?.userJid ?? '').trim()
  if (!userJid) return false
  const ownJids = [state.socket?.user?.id, state.socket?.user?.lid].map(value => String(value ?? '').trim()).filter(Boolean)
  if (ownJids.includes(userJid)) return false
  const resolved = await phoneJidFromAnyJid(userJid)
  return phoneFromJid(resolved) === String(expectedPhone ?? '').replace(/\D/g, '')
}

function firstNonEmpty(...values) {
  return values.map(value => String(value ?? '').trim()).find(Boolean) ?? ''
}

function syncTypeName(value) {
  const names = ['initial_bootstrap', 'initial_status', 'full', 'recent', 'push_name', 'non_blocking_data', 'on_demand']
  const numeric = Number(value)
  return Number.isInteger(numeric) && names[numeric] ? names[numeric] : String(value ?? 'unknown')
}

function emitItems(event, items, source = 'live', extra = {}, chunkSize = 100) {
  for (let offset = 0; offset < items.length; offset += chunkSize) {
    emit({
      type: 'event', event, accountId: state.accountId,
      data: { items: items.slice(offset, offset + chunkSize), source, ...extra }
    })
  }
}

function isStatusUpdateMessage(webMessage) {
  const raw = webMessage?.message ?? webMessage
  const context = messageContextInfo(raw)
  return Boolean(
    raw?.statusMentionMessage
    || raw?.groupStatusMentionMessage
    || webMessage?.statusMentionMessageInfo?.quotedStatus
    || webMessage?.isMentionedInStatus
    || webMessage?.key?.remoteJid === 'status@broadcast'
    || context?.remoteJid === 'status@broadcast'
    || context?.isMentionedInStatus
  )
}

function messageContextInfo(message) {
  message = messageContent(message)
  if (!message) return null
  return message.extendedTextMessage?.contextInfo
    ?? message.imageMessage?.contextInfo
    ?? message.videoMessage?.contextInfo
    ?? message.audioMessage?.contextInfo
    ?? message.documentMessage?.contextInfo
    ?? message.stickerMessage?.contextInfo
    ?? null
}

function revocationTarget(message) {
  const protocol = messageContent(message)?.protocolMessage
  if (protocol?.type !== proto.Message.ProtocolMessage.Type.REVOKE || !protocol.key?.id) return null
  return protocol.key
}

function rememberMessage(message) {
  const id = String(message?.key?.id ?? '')
  if (!id) return
  const existing = state.messages.get(id)
  if (existing?.message && !message?.message) {
    message = {
      ...existing,
      ...message,
      key: { ...(existing.key ?? {}), ...(message?.key ?? {}) },
      message: existing.message
    }
  }
  state.messages.delete(id)
  state.messages.set(id, message)
  while (state.messages.size > 10000) state.messages.delete(state.messages.keys().next().value)
}

function quotedMessageDetails(message) {
  const contextInfo = messageContextInfo(message)
  const quotedMessageId = String(contextInfo?.stanzaId ?? '')
  if (!quotedMessageId) return { quotedMessageId: '', quotedText: '', quotedFromMe: false }
  const participantPhone = phoneFromJid(String(contextInfo?.participant ?? ''))
  const ownPhone = phoneFromJid(String(state.socket?.user?.id ?? ''))
  return {
    quotedMessageId,
    quotedText: messageText(contextInfo?.quotedMessage),
    quotedFromMe: Boolean(participantPhone && ownPhone && participantPhone === ownPhone)
  }
}

function quotedSendOptions(command, jid) {
  const quotedMessageId = String(command.quotedMessageId ?? '').trim()
  if (!quotedMessageId) return {}
  let quoted = state.messages.get(quotedMessageId)
  if (!quoted) {
    const quotedText = String(command.quotedText ?? '').trim()
    if (!quotedText) throw new Error('quoted_message_not_available')
    quoted = {
      key: { remoteJid: jid, id: quotedMessageId, fromMe: Boolean(command.quotedFromMe) },
      message: { conversation: quotedText }
    }
  }
  return { quoted }
}

function messageFileName(message) {
  message = messageContent(message)
  return firstNonEmpty(
    message?.documentMessage?.fileName,
    message?.imageMessage?.fileName,
    message?.videoMessage?.fileName,
    message?.audioMessage?.fileName
  )
}

function messageMimeType(message) {
  message = messageContent(message)
  return firstNonEmpty(
    message?.documentMessage?.mimetype,
    message?.imageMessage?.mimetype,
    message?.videoMessage?.mimetype,
    message?.audioMessage?.mimetype,
    message?.stickerMessage?.mimetype
  )
}

function messageFileLength(message) {
  message = messageContent(message)
  const value = message?.documentMessage?.fileLength
    ?? message?.imageMessage?.fileLength
    ?? message?.videoMessage?.fileLength
    ?? message?.audioMessage?.fileLength
    ?? message?.stickerMessage?.fileLength
  const numeric = Number(value?.toString?.() ?? value)
  return Number.isFinite(numeric) ? numeric : 0
}

const mediaExtensions = new Map(Object.entries({
  'image/jpeg': '.jpg', 'image/png': '.png', 'image/webp': '.webp', 'image/gif': '.gif',
  'video/mp4': '.mp4', 'video/3gpp': '.3gp', 'video/quicktime': '.mov',
  'audio/mpeg': '.mp3', 'audio/mp4': '.m4a', 'audio/ogg': '.ogg', 'audio/wav': '.wav', 'audio/aac': '.aac',
  'application/pdf': '.pdf', 'text/plain': '.txt', 'text/csv': '.csv', 'application/json': '.json',
  'application/msword': '.doc', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document': '.docx',
  'application/vnd.ms-excel': '.xls', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': '.xlsx',
  'application/vnd.ms-powerpoint': '.ppt', 'application/vnd.openxmlformats-officedocument.presentationml.presentation': '.pptx',
  'application/zip': '.zip', 'application/vnd.rar': '.rar', 'application/x-7z-compressed': '.7z'
}))

function safeMediaFileName(value) {
  const safe = String(value ?? '').replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').replace(/[. ]+$/g, '').trim()
  return (safe || 'media').slice(0, 120)
}

function fallbackMediaExtension(kind, mimeType) {
  const normalizedMime = String(mimeType ?? '').split(';')[0].trim().toLowerCase()
  return mediaExtensions.get(normalizedMime) ?? ({ image: '.jpg', video: '.mp4', audio: '.ogg', sticker: '.webp', document: '.bin' }[kind] ?? '.bin')
}

async function downloadMessageMedia(message, kind, fileName, mimeType) {
  if (!['image', 'video', 'audio', 'document', 'sticker'].includes(kind)) return { mediaPath: '', mediaDownloadError: '' }
  const announcedLength = messageFileLength(message.message)
  if (announcedLength > 100 * 1024 * 1024) return { mediaPath: '', mediaDownloadError: '媒体超过 100MB，未自动下载' }

  const messageId = safeMediaFileName(message?.key?.id ?? crypto.randomUUID())
  const extension = path.extname(fileName || '') || fallbackMediaExtension(kind, mimeType)
  const displayName = safeMediaFileName(fileName || `${kind}${extension}`)
  const directory = path.join(resolveDataRoot(), 'whatsapp-media', safeMediaFileName(state.accountId))
  const destination = path.join(directory, `${messageId}-${displayName}`)
  const downloadKey = `${state.accountId}:${messageId}`

  try {
    const existing = await fs.stat(destination)
    if (existing.isFile() && existing.size > 0) return { mediaPath: destination, mediaDownloadError: '' }
  } catch { }

  if (!state.mediaDownloads.has(downloadKey)) {
    state.mediaDownloads.set(downloadKey, (async () => {
      await fs.mkdir(directory, { recursive: true })
      const context = state.socket?.updateMediaMessage
        ? { logger, reuploadRequest: item => state.socket.updateMediaMessage(item) }
        : undefined
      const buffer = await downloadMediaMessage(message, 'buffer', {}, context)
      if (!Buffer.isBuffer(buffer) || buffer.length === 0) throw new Error('empty_media_download')
      if (buffer.length > 100 * 1024 * 1024) throw new Error('media_exceeds_100mb')
      const temporary = `${destination}.${process.pid}.tmp`
      await fs.writeFile(temporary, buffer, { mode: 0o600 })
      await fs.rm(destination, { force: true })
      await fs.rename(temporary, destination)
      return destination
    })().finally(() => state.mediaDownloads.delete(downloadKey)))
  }

  try { return { mediaPath: await state.mediaDownloads.get(downloadKey), mediaDownloadError: '' } }
  catch (error) { return { mediaPath: '', mediaDownloadError: safeError(error) } }
}

const mediaTypes = new Map(Object.entries({
  '.jpg': ['image', 'image/jpeg'], '.jpeg': ['image', 'image/jpeg'], '.png': ['image', 'image/png'], '.webp': ['image', 'image/webp'], '.gif': ['video', 'image/gif'],
  '.mp4': ['video', 'video/mp4'], '.3gp': ['video', 'video/3gpp'], '.mov': ['video', 'video/quicktime'],
  '.mp3': ['audio', 'audio/mpeg'], '.m4a': ['audio', 'audio/mp4'], '.ogg': ['audio', 'audio/ogg'], '.opus': ['audio', 'audio/ogg; codecs=opus'], '.wav': ['audio', 'audio/wav'], '.aac': ['audio', 'audio/aac'],
  '.pdf': ['document', 'application/pdf'], '.txt': ['document', 'text/plain'], '.csv': ['document', 'text/csv'], '.json': ['document', 'application/json'],
  '.doc': ['document', 'application/msword'], '.docx': ['document', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
  '.xls': ['document', 'application/vnd.ms-excel'], '.xlsx': ['document', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
  '.ppt': ['document', 'application/vnd.ms-powerpoint'], '.pptx': ['document', 'application/vnd.openxmlformats-officedocument.presentationml.presentation'],
  '.zip': ['document', 'application/zip'], '.rar': ['document', 'application/vnd.rar'], '.7z': ['document', 'application/x-7z-compressed']
}))

async function buildMediaMessage(filePath, caption) {
  const resolved = path.resolve(String(filePath ?? ''))
  const info = await fs.stat(resolved)
  if (!info.isFile()) throw new Error('attachment_is_not_a_file')
  if (info.size <= 0 || info.size > 100 * 1024 * 1024) throw new Error('attachment_size_must_be_between_1_byte_and_100mb')
  const fileName = path.basename(resolved)
  const extension = path.extname(fileName).toLowerCase()
  const mediaType = mediaTypes.get(extension)
  if (!mediaType) throw new Error('unsupported_attachment_type')
  const [kind, mimeType] = mediaType
  const data = await fs.readFile(resolved)
  const safeCaption = String(caption ?? '').trim().slice(0, 1024)
  if (kind === 'image') return { payload: { image: data, mimetype: mimeType, caption: safeCaption }, kind, mimeType, fileName }
  if (kind === 'video') return { payload: { video: data, mimetype: mimeType, caption: safeCaption }, kind, mimeType, fileName }
  if (kind === 'audio') return { payload: { audio: data, mimetype: mimeType, ptt: false }, kind, mimeType, fileName }
  return { payload: { document: data, mimetype: mimeType, fileName, caption: safeCaption }, kind, mimeType, fileName }
}

function shouldForward(jid) {
  return Boolean(jid)
    && (jid.endsWith('@s.whatsapp.net') || jid.endsWith('@lid'))
    && jid !== 'status@broadcast'
}

function shouldIngest(jid) {
  return Boolean(jid)
    && isSupportedInboundJid(jid)
    && jid !== 'status@broadcast'
}

async function resolveConversationJid(key) {
  const group = normalizeGroupJid(key?.remoteJidAlt) || normalizeGroupJid(key?.remoteJid)
  return group || await resolveDirectJid(key)
}

async function resolveDirectJid(key) {
  const alternate = normalizeOutboundUserJid(key?.remoteJidAlt)
  const remote = normalizeOutboundUserJid(key?.remoteJid)
  if (alternate.endsWith('@s.whatsapp.net')) return alternate
  if (remote.endsWith('@s.whatsapp.net')) return remote
  const lid = alternate.endsWith('@lid') ? alternate : remote.endsWith('@lid') ? remote : ''
  if (!lid) return remote || alternate
  try { return normalizeOutboundUserJid(await state.socket?.signalRepository?.lidMapping?.getPNForLID(lid)) || lid }
  catch { return lid }
}

async function resolveUserJid(...values) {
  const candidates = values.flat().map(normalizeOutboundUserJid).filter(Boolean)
  const phoneJid = candidates.find(value => value.endsWith('@s.whatsapp.net'))
  if (phoneJid) return phoneJid
  const lid = candidates.find(value => value.endsWith('@lid'))
  if (!lid) return ''
  try { return normalizeOutboundUserJid(await state.socket?.signalRepository?.lidMapping?.getPNForLID(lid)) || lid }
  catch { return lid }
}

async function normalizeContact(contact, source = 'live') {
  const sourceJid = String(contact?.id ?? contact?.lid ?? contact?.phoneNumber ?? '')
  const jid = await resolveUserJid(contact?.phoneNumber, contact?.id, contact?.lid)
  if (!shouldForward(jid || sourceJid)) return null
  const phone = phoneFromJid(jid)
  const displayName = firstNonEmpty(contact?.name, contact?.notify, contact?.verifiedName, contact?.username, phone ? `+${phone}` : sourceJid)
  return {
    jid: jid || sourceJid,
    sourceJid,
    phone,
    displayName,
    savedName: String(contact?.name ?? ''),
    notifyName: String(contact?.notify ?? ''),
    verifiedName: String(contact?.verifiedName ?? ''),
    username: String(contact?.username ?? ''),
    source
  }
}

async function normalizeChat(chat, source = 'live') {
  const sourceJid = String(chat?.id ?? chat?.lidJid ?? chat?.pnJid ?? '')
  const groupJid = normalizeGroupJid(chat?.id) || normalizeGroupJid(chat?.jid)
  if (groupJid) {
    const embedded = latestChatAnchor(chat)
    const timestamp = chat?.conversationTimestamp ?? chat?.lastMsgTimestamp ?? chat?.lastMessageRecvTimestamp
    return {
      jid: groupJid,
      sourceJid: groupJid,
      groupJid,
      phone: '',
      isGroup: true,
      displayName: firstNonEmpty(chat?.subject, chat?.name, chat?.displayName, 'WhatsApp 群聊'),
      lastMessage: embedded ? messageText(embedded.message) : '',
      lastMessageAt: timestamp == null ? '' : timestampToIso(timestamp),
      unreadCount: Number.isFinite(Number(chat?.unreadCount)) ? Number(chat.unreadCount) : null,
      archived: Boolean(chat?.archived),
      ...(chat?.pinned !== undefined && chat?.pinned !== null ? {
        pinned: Number(chat.pinned) > 0,
        pinnedAt: Number(chat.pinned) > 0 ? timestampToIso(chat.pinned) : ''
      } : {}),
      source
    }
  }
  const jid = await resolveUserJid(chat?.pnJid, chat?.id, chat?.lidJid)
  if (!shouldForward(jid || sourceJid)) return null
  const phone = phoneFromJid(jid)
  if (!phone) return null
  const cachedContact = [...state.contacts.values()].find(item => item.phone === phone || item.jid === jid || item.sourceJid === sourceJid)
  const embedded = latestChatAnchor(chat)
  const timestamp = chat?.conversationTimestamp ?? chat?.lastMsgTimestamp ?? chat?.lastMessageRecvTimestamp
  return {
    jid,
    sourceJid,
    phone,
    displayName: firstNonEmpty(cachedContact?.savedName, cachedContact?.displayName, chat?.name, chat?.displayName, `+${phone}`),
    lastMessage: embedded ? messageText(embedded.message) : '',
    lastMessageAt: timestamp == null ? '' : timestampToIso(timestamp),
    unreadCount: Number.isFinite(Number(chat?.unreadCount)) ? Number(chat.unreadCount) : null,
    archived: Boolean(chat?.archived),
    ...(chat?.pinned !== undefined && chat?.pinned !== null ? {
      pinned: Number(chat.pinned) > 0,
      pinnedAt: Number(chat.pinned) > 0 ? timestampToIso(chat.pinned) : ''
    } : {}),
    source
  }
}

async function resolveGroupDisplayName(groupJid) {
  const jid = normalizeGroupJid(groupJid)
  if (!jid) return ''
  const cached = state.chats.get(jid)
  if (cached?.displayName && cached.displayName !== 'WhatsApp 群聊') return cached.displayName
  try {
    const metadata = await state.socket?.groupMetadata?.(jid)
    const displayName = firstNonEmpty(metadata?.subject, cached?.displayName, 'WhatsApp 群聊')
    rememberChat({
      jid,
      sourceJid: jid,
      groupJid: jid,
      phone: '',
      isGroup: true,
      displayName,
      source: 'group_metadata'
    })
    return displayName
  } catch {
    return firstNonEmpty(cached?.displayName, 'WhatsApp 群聊')
  }
}

async function normalizeMessage(message, source) {
  rememberMessage(message)
  const sourceJid = message?.key?.remoteJid ?? ''
  const jid = await resolveConversationJid(message?.key)
  if (!shouldIngest(jid)) return null
  const isGroup = isGroupJid(jid)
  const groupName = isGroup ? await resolveGroupDisplayName(jid) : ''
  const rawParticipantJid = String(message?.key?.participantAlt ?? message?.key?.participant ?? message?.participant ?? '')
  const participantJid = isGroup
    ? await resolveUserJid(message?.key?.participantAlt, message?.key?.participant, message?.participant)
      || normalizeOutboundUserJid(rawParticipantJid)
    : ''
  const participantPhone = isGroup ? phoneFromJid(await phoneJidFromAnyJid(participantJid)) : ''
  const participantContact = isGroup
    ? [...state.contacts.values()].find(item =>
        (participantPhone && item.phone === participantPhone)
        || item.jid === participantJid
        || item.sourceJid === rawParticipantJid)
    : null
  const participantName = isGroup
    ? firstNonEmpty(message?.pushName, participantContact?.savedName, participantContact?.displayName, participantPhone ? `+${participantPhone}` : '', '群成员')
    : ''
  const isStatusUpdate = isStatusUpdateMessage(message)
  const revokedKey = revocationTarget(message?.message)
  if (revokedKey) {
    state.messages.delete(String(revokedKey.id))
    return {
      id: message.key.id ?? '',
      jid,
      sourceJid,
      groupJid: isGroup ? jid : '',
      groupName,
      phone: isGroup ? '' : phoneFromJid(jid),
      isGroup,
      fromMe: Boolean(revokedKey.fromMe),
      participantJid,
      participantPhone,
      participantName,
      revokedMessageId: String(revokedKey.id),
      isRevocation: true,
      timestamp: timestampToIso(message.messageTimestamp),
      source
    }
  }
  const displayable = isDisplayableMessage(message.message)
  const recoveryPending = message.messageStubType === proto.WebMessageInfo.StubType.CIPHERTEXT && !displayable
  // Undecryptable or unsupported content is surfaced as a placeholder instead
  // of being dropped: freshly paired web devices often miss the group sender
  // key, and once the key arrives the next messages decrypt normally. Dropping
  // here made entire group chats invisible (5.18.15 fix).
  const kind = displayable || recoveryPending ? (recoveryPending ? 'unavailable' : messageKind(message.message)) : 'unavailable'
  const fileName = displayable ? messageFileName(message.message) : ''
  const mimeType = displayable ? messageMimeType(message.message) : ''
  const media = await downloadMessageMedia(message, kind, fileName, mimeType)
  const quote = quotedMessageDetails(message.message)
  const timestamp = timestampToIso(message.messageTimestamp)
  const expectedPhone = phoneFromJid(jid)
  const verifiedReceipts = []
  if (message.key.fromMe && expectedPhone) {
    for (const receipt of message.userReceipt ?? []) {
      if (await receiptBelongsToPhone(receipt, expectedPhone)) verifiedReceipts.push(receipt)
    }
  }
  return {
    id: message.key.id ?? '',
    jid,
    sourceJid,
    groupJid: isGroup ? jid : '',
    groupName,
    phone: isGroup ? '' : phoneFromJid(jid),
    isGroup,
    fromMe: Boolean(message.key.fromMe),
    participant: rawParticipantJid,
    participantJid,
    participantPhone,
    participantName,
    pushName: message.pushName ?? '',
    timestamp,
    text: messageText(message.message),
    kind,
    recoveryPending,
    fileName,
    mimeType,
    ...media,
    status: message.status ?? null,
    deliveredAt: latestReceiptTime(verifiedReceipts, 'receiptTimestamp'),
    readAt: latestReceiptTime(verifiedReceipts, 'readTimestamp', 'playedTimestamp'),
    isStatusUpdate,
    statusExpiresAt: isStatusUpdate ? new Date(new Date(timestamp).getTime() + 24 * 60 * 60 * 1000).toISOString() : '',
    ...quote,
    source
  }
}

function latestReceiptTime(receipts, ...fields) {
  let latest = null
  for (const receipt of receipts ?? []) for (const field of fields) {
    const value = receipt?.[field]
    if (value == null) continue
    const numeric = Number(value?.toString?.() ?? value)
    if (Number.isFinite(numeric) && (latest == null || numeric > latest)) latest = numeric
  }
  return latest == null ? '' : timestampToIso(latest)
}

function rememberContact(contact) {
  if (!contact) return
  const key = contact.sourceJid || contact.jid || contact.phone
  const existing = state.contacts.get(key) ?? {}
  const merged = { ...existing }
  for (const [name, value] of Object.entries(contact)) if (value !== '' && value != null) merged[name] = value
  merged.displayName = firstNonEmpty(merged.savedName, merged.notifyName, merged.verifiedName, merged.username, merged.displayName, merged.phone ? `+${merged.phone}` : key)
  state.contacts.set(key, merged)
}

function rememberChat(chat) {
  if (!chat) return
  const key = chat.phone || chat.jid
  if (!key) return
  const existing = state.chats.get(key) ?? {}
  state.chats.set(key, {
    ...existing,
    ...chat,
    displayName: chat.displayName || existing.displayName || (chat.isGroup ? 'WhatsApp 群聊' : `+${chat.phone}`),
    lastMessage: chat.lastMessage || existing.lastMessage || '',
    lastMessageAt: chat.lastMessageAt || existing.lastMessageAt || ''
  })
}

async function normalizeContacts(contacts, source) {
  const items = (await Promise.all((contacts ?? []).map(contact => normalizeContact(contact, source)))).filter(Boolean)
  for (const item of items) rememberContact(item)
  return items
}

async function normalizeChats(chats, source) {
  const items = (await Promise.all((chats ?? []).map(chat => normalizeChat(chat, source)))).filter(Boolean)
  for (const item of items) rememberChat(item)
  return items
}

async function emitEmbeddedChatMessages(chats, source) {
  const seen = new Set()
  const embedded = (chats ?? []).flatMap(embeddedChatMessages).filter(message => {
    const id = String(message?.key?.id ?? '')
    if (!id || seen.has(id) || state.messages.has(id)) return false
    seen.add(id)
    return true
  })
  if (embedded.length === 0) return []
  const items = await normalizeMessages(embedded, `${source}:chat_anchor`)
  if (items.length > 0) {
    state.historyTotals.messages += items.length
    emitItems('messages_history', items, `${source}:chat_anchor`)
    getOfflineCatchupCoordinator().noteRecoveredMessages(items.length)
  }
  return items
}

async function requestMissingChatHistory(chats, source) {
  if (!state.historyRecovery.active || !state.socket || state.connection !== 'connected') return 0
  if (String(source).toLowerCase().includes('on_demand')) return 0

  const requests = []
  for (const chat of chats ?? []) {
    const anchor = latestChatAnchor(chat)
    if (!anchor) continue
    const normalized = await normalizeChat(chat, source)
    if (!normalized) continue
    const candidate = { ...chat, id: normalized.jid, jid: normalized.jid, phone: normalized.phone }
    const cursor = findHistoryCursor(state.historyRecovery.cursorIndex, candidate)
    if (!shouldRequestChatHistory(chat, cursor, anchor)) continue
    const key = historyRequestKey(anchor)
    if (!key || state.historyRecovery.requested.has(key)) continue
    state.historyRecovery.requested.add(key)
    requests.push({ anchor, key })
  }

  let requested = 0
  let cursor = 0
  const workers = Array.from({ length: Math.min(3, Math.max(1, requests.length)) }, async () => {
    while (cursor < requests.length) {
      const item = requests[cursor++]
      try {
        await state.socket.fetchMessageHistory(50, item.anchor.key, anchorTimestamp(item.anchor))
        requested++
      } catch (error) {
        emit({
          type: 'event', event: 'connection_issue', accountId: state.accountId,
          data: {
            code: 'chat_history_request_failed', recoverable: true,
            message: '个别 WhatsApp 会话的离线历史请求暂未送达，程序将保持连接并继续重试',
            jid: String(item.anchor?.key?.remoteJid ?? ''), error: safeError(error)
          }
        })
      }
    }
  })
  await Promise.all(workers)
  if (requested > 0) getOfflineCatchupCoordinator().noteHistoryRequest(requested)
  return requested
}

async function processChats(chats, source) {
  const items = await normalizeChats(chats, source)
  emitItems('chats_upsert', items, source)
  await emitEmbeddedChatMessages(chats, source)
  await requestMissingChatHistory(chats, source)
  return items
}

async function discoverParticipatingGroups(source) {
  if (!state.socket?.groupFetchAllParticipating) return 0
  try {
    const groups = Object.values(await state.socket.groupFetchAllParticipating() ?? {})
    if (groups.length === 0) return 0
    const items = await normalizeChats(groups, source)
    emitItems('chats_upsert', items, source)
    return items.length
  } catch (error) {
    emit({
      type: 'event', event: 'connection_issue', accountId: state.accountId,
      data: {
        code: 'group_discovery_failed', recoverable: true,
        message: 'WhatsApp 群聊清单暂未完整返回，程序将保持连接并继续同步', error: safeError(error)
      }
    })
    return 0
  }
}

async function normalizeMessages(messages, source) {
  const input = messages ?? []
  const normalized = new Array(input.length)
  let cursor = 0
  const workers = Array.from({ length: Math.min(3, Math.max(1, input.length)) }, async () => {
    while (cursor < input.length) {
      const index = cursor++
      normalized[index] = await normalizeMessage(input[index], source)
    }
  })
  await Promise.all(workers)
  const items = normalized.filter(item => item?.id && (item.phone || item.isGroup))
  for (const item of items) {
    if (item.isRevocation) continue
    const contact = item.isGroup ? null : [...state.contacts.values()].find(value => value.phone === item.phone)
    if (!item.isGroup && !item.fromMe && item.pushName) rememberContact({ jid: item.jid, sourceJid: item.sourceJid, phone: item.phone, displayName: item.pushName, notifyName: item.pushName, source })
    const preview = messagePreview(item)
    const lastMessage = item.isGroup && !item.fromMe
      ? `${item.participantName || '群成员'}：${preview}`
      : item.isStatusUpdate ? `[最新动态] ${preview}` : preview
    rememberChat({
      jid: item.jid,
      sourceJid: item.sourceJid,
      groupJid: item.groupJid,
      phone: item.phone,
      isGroup: item.isGroup,
      displayName: item.isGroup ? item.groupName : contact?.displayName || item.pushName || `+${item.phone}`,
      lastMessage,
      lastMessageAt: item.timestamp,
      unreadCount: null,
      source
    })
  }
  return items
}

async function forwardMessage(message, source) {
  const data = await normalizeMessage(message, source)
  if (!data?.id || (!data.phone && !data.isGroup)) return
  if (data.isRevocation) {
    emit({ type: 'event', event: 'message_revoked', accountId: state.accountId, data })
    return
  }
  if (!data.isGroup && !data.fromMe && data.pushName) rememberContact({ jid: data.jid, sourceJid: data.sourceJid, phone: data.phone, displayName: data.pushName, notifyName: data.pushName, source })
  const contact = data.isGroup ? null : [...state.contacts.values()].find(value => value.phone === data.phone)
  const preview = messagePreview(data)
  const lastMessage = data.isGroup && !data.fromMe
    ? `${data.participantName || '群成员'}：${preview}`
    : data.isStatusUpdate ? `[最新动态] ${preview}` : preview
  rememberChat({
    jid: data.jid,
    sourceJid: data.sourceJid,
    groupJid: data.groupJid,
    phone: data.phone,
    isGroup: data.isGroup,
    displayName: data.isGroup ? data.groupName : contact?.displayName || data.pushName || `+${data.phone}`,
    lastMessage,
    lastMessageAt: data.timestamp,
    unreadCount: null,
    source
  })
  emit({
    type: 'event',
    event: 'message',
    accountId: state.accountId,
    data
  })
}

function messagePreview(message) {
  if (message.text) return message.text
  return ({
    image: '[图片]', video: '[视频]', audio: '[音频]', document: '[文件]', sticker: '[贴图]',
    contact: '[联系人]', location: '[位置]', poll: '[投票]', reaction: '[表情回应]', event: '[活动]',
    unavailable: '[正在从手机恢复消息内容]', unknown: '[消息内容未同步成功]'
  })[message.kind] ?? '[暂不支持的 WhatsApp 消息]'
}

async function handleHistorySync(update) {
  const phase = syncTypeName(update?.syncType)
  emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'syncing', phase, progress: update?.progress ?? null } })
  const contacts = await normalizeContacts(update?.contacts, `history:${phase}`)
  const chats = await normalizeChats(update?.chats, `history:${phase}`)
  const embeddedMessages = await emitEmbeddedChatMessages(update?.chats, `history:${phase}`)
  const seen = new Set()
  const historyMessages = (update?.messages ?? []).filter(message => {
    const id = String(message?.key?.id ?? '')
    if (!id || seen.has(id) || state.messages.has(id)) return false
    seen.add(id)
    return true
  })
  const messages = await normalizeMessages(historyMessages, `history:${phase}`)
  state.historyTotals.contacts += contacts.length
  state.historyTotals.chats += chats.length
  state.historyTotals.messages += messages.length
  emitItems('contacts_upsert', contacts, `history:${phase}`)
  emitItems('chats_upsert', chats, `history:${phase}`)
  emitItems('messages_history', messages, `history:${phase}`)
  if (messages.length > 0) getOfflineCatchupCoordinator().noteRecoveredMessages(messages.length)
  await requestMissingChatHistory(update?.chats, `history:${phase}`)
  if (update?.isLatest && !state.existingSession) state.historyRecovery.active = false
  emit({
    type: 'event', event: 'sync_status', accountId: state.accountId,
    data: {
      state: update?.isLatest ? 'complete' : 'syncing', phase, progress: update?.progress ?? null,
      contacts: state.contacts.size, chats: state.chats.size, messages: state.historyTotals.messages,
      embeddedMessages: embeddedMessages.length,
      isLatest: Boolean(update?.isLatest)
    }
  })
}

async function emitCachedSnapshot(source = 'manual') {
  const contacts = [...state.contacts.values()]
  const chats = [...state.chats.values()]
  emitItems('contacts_upsert', contacts, source)
  emitItems('chats_upsert', chats, source)
  return { contacts: contacts.length, chats: chats.length }
}

let offlineCatchupCoordinator = null

function getOfflineCatchupCoordinator() {
  offlineCatchupCoordinator ??= new OfflineCatchupCoordinator({
    enqueue: action => enqueueSync(action, 'offline_messages'),
    emitStatus: data => {
      if (data?.state === 'complete') state.historyRecovery.active = false
      emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data })
    },
    emitIssue: data => emit({
      type: 'event', event: 'connection_issue', accountId: state.accountId,
      data: { ...data, error: safeError(data.error) }
    }),
    emitSnapshot: source => emitCachedSnapshot(source),
    getTotals: () => ({ messages: state.historyTotals.messages, existingSession: state.existingSession })
  })
  return offlineCatchupCoordinator
}

async function manualSync() {
  emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'syncing', phase: 'app_state', progress: null } })
  try {
    await state.socket?.resyncAppState?.(ALL_WA_PATCH_NAMES, false)
    const groupCount = await discoverParticipatingGroups('manual')
    const counts = await emitCachedSnapshot('manual')
    emit({
      type: 'event', event: 'sync_status', accountId: state.accountId,
      data: { state: 'complete', phase: 'app_state', progress: 100, ...counts, messages: state.historyTotals.messages, existingSession: state.existingSession, groups: groupCount }
    })
  } catch (error) {
    emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'failed', phase: 'app_state', error: safeError(error), existingSession: state.existingSession } })
  }
}

async function catchUpOfflineMessages(cursors = []) {
  // On-demand history requests (fetchMessageHistory) work for web sessions
  // too — the old guard skipped them entirely, so group chats and offline
  // messages never backfilled for web-paired accounts (5.18.15 fix).
  emit({
    type: 'event', event: 'sync_status', accountId: state.accountId,
    data: { state: 'syncing', phase: 'offline_messages', progress: null, source: 'manual' }
  })
  try {
    state.historyRecovery = {
      active: true,
      cursorIndex: normalizeHistoryCursors(cursors),
      requested: new Set(),
      source: 'manual'
    }
    await state.socket?.resyncAppState?.(ALL_WA_PATCH_NAMES, false)
    await emitCachedSnapshot('manual:before_catchup')
    await connect('manual')
  } catch (error) {
    emit({
      type: 'event', event: 'sync_status', accountId: state.accountId,
      data: { state: 'failed', phase: 'offline_messages', error: safeError(error), existingSession: state.existingSession }
    })
  }
}

async function closeSocket() {
  getOfflineCatchupCoordinator().cancel()
  state.connectionGeneration += 1
  if (state.reconnectTimer) clearTimeout(state.reconnectTimer)
  state.reconnectTimer = null
  if (state.pairingTimer) clearTimeout(state.pairingTimer)
  state.pairingTimer = null
  const socket = state.socket
  state.socket = null
  if (socket) {
    try { socket.end(new Error('waflow_disconnect')) } catch { }
  }
  state.connection = 'disconnected'
}

async function resetSessionForQr() {
  if (!state.sessionDir) return
  await fs.rm(state.sessionDir, { recursive: true, force: true })
  await fs.mkdir(state.sessionDir, { recursive: true })
  state.existingSession = false
  state.desktopHistoryProfile = false
  state.newDesktopPairing = true
  state.desktopUpgradeRequested = false
  state.desktopUpgradeFailures = 0
}

async function connect(catchupSource = 'startup') {
  if (!state.sessionDir) throw new Error('bridge_not_initialized')
  await closeSocket()
  if (catchupSource === 'reconnect' && state.manualDisconnect) return
  state.manualDisconnect = false
  state.qrSeen = false
  const generation = state.connectionGeneration
  const attempt = ++state.connectionAttempt
  state.pendingNotificationsAttempt = 0
  if (!state.historyRecovery.active) {
    state.historyRecovery = {
      active: true,
      cursorIndex: normalizeHistoryCursors([]),
      requested: new Set(),
      source: catchupSource
    }
  } else {
    state.historyRecovery.requested = new Set()
    state.historyRecovery.source = catchupSource
  }
  emit({
    type: 'event', event: 'connection_stage', accountId: state.accountId,
    data: { state: 'preparing', attempt, message: '正在准备安全登录会话' }
  })
  await fs.mkdir(state.sessionDir, { recursive: true })
  if (!state.authKey) throw new Error('session_encryption_key_missing')
  const { state: auth, saveCreds } = await loadAuthStateWithRecovery()
  state.existingSession = Boolean(auth.creds.registered)
  state.newDesktopPairing = !state.existingSession
  state.desktopHistoryProfile = state.existingSession ? await hasDesktopHistoryProfile() : false
  state.contacts.clear()
  state.chats.clear()
  state.messages.clear()
  state.historyTotals = { contacts: 0, chats: 0, messages: 0 }
  emit({
    type: 'event', event: 'connection_stage', accountId: state.accountId,
    data: { state: 'checking_protocol', attempt, message: '正在检查 WhatsApp 兼容协议' }
  })
  const cachedProtocolVersion = await readCachedProtocolVersion()
  const versionInfo = await resolveBaileysVersion([
    { source: 'whatsapp_web', fetch: fetchLatestWaWebVersion },
    { source: 'baileys', fetch: fetchLatestBaileysVersion }
  ], { cachedVersion: cachedProtocolVersion, timeoutMs: VERSION_LOOKUP_TIMEOUT_MS })
  if (versionInfo.source === 'whatsapp_web' || versionInfo.source === 'baileys') {
    await cacheProtocolVersion(versionInfo.version, versionInfo.source).catch(() => {})
  }
  const networkAgent = createProxyAgent(state.currentProxyUrl)
  const routeLabel = safeProxyLabel(state.currentProxyUrl, state.proxySource)
  emit({
    type: 'event', event: 'connection_stage', accountId: state.accountId,
    data: {
      state: 'opening',
      attempt,
      versionSource: versionInfo.source,
      clientProfile: state.desktopUpgradeRequested || state.desktopHistoryProfile
        ? 'desktop_history'
        : 'windows_chrome_pairing',
      warning: versionInfo.warning,
      message: versionInfo.source === 'whatsapp_web' || versionInfo.source === 'baileys'
        ? `正在通过${routeLabel}建立 WhatsApp 安全连接`
        : versionInfo.source === 'cached'
          ? `在线协议检查暂不可用，正在通过${routeLabel}使用最近成功协议连接`
          : `在线协议检查不可用，正在通过${routeLabel}使用内置兼容协议连接`
    }
  })
  // Only a session that was previously established as a Desktop companion
  // (marker file) reconnects with the Desktop profile. Fresh QR pairings are
  // web sessions — WhatsApp rejects Desktop-profile reconnects of web-paired
  // sessions with status 428, so desktopUpgradeRequested no longer selects
  // the Desktop profile.
  const useDesktopProfile = state.existingSession && state.desktopHistoryProfile
  const socket = makeWASocket({
    auth,
    ...(versionInfo.version ? { version: versionInfo.version } : {}),
    // WhatsApp currently rejects a brand-new unregistered session that claims
    // to be a Desktop companion (status 428), before it can emit a QR. Pair as
    // the Windows web client first; after the phone accepts the QR, the open
    // handler reconnects the registered session with the Desktop profile so
    // full-history recovery remains available. If that Desktop upgrade keeps
    // failing, the close handler clears desktopUpgradeRequested so subsequent
    // reconnects fall back to the already-accepted web profile instead of
    // looping forever on the Desktop profile. A web-profile session must not
    // request full history (syncFullHistory) — WhatsApp rejects that request
    // for non-Desktop companions, which is what kept the fallback reconnect
    // stuck in a retry loop.
    browser: useDesktopProfile ? Browsers.macOS('Desktop') : Browsers.windows('Chrome'),
    logger,
    printQRInTerminal: false,
    markOnlineOnConnect: false,
    syncFullHistory: useDesktopProfile,
    connectTimeoutMs: 20000,
    defaultQueryTimeoutMs: 30000,
    qrTimeout: 60000,
    ...(networkAgent ? { agent: networkAgent, fetchAgent: networkAgent } : {}),
    shouldSyncHistoryMessage: () => true,
    generateHighQualityLinkPreview: false
  })
  state.socket = socket
  state.connection = 'connecting'
  emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'connecting' } })
  state.pairingTimer = setTimeout(() => {
    if (state.connectionGeneration !== generation || state.socket !== socket || state.connection !== 'connecting' || state.qrSeen) return
    if (state.currentProxyUrl && state.allowDirectFallback && !state.directFallbackUsed) {
      state.directFallbackUsed = true
      state.currentProxyUrl = ''
      state.immediateReconnect = true
      state.connection = 'retrying'
      emit({
        type: 'event', event: 'connection_issue', accountId: state.accountId,
        data: {
          code: 'proxy_route_timeout',
          recoverable: true,
          attempt,
          message: 'Windows 系统代理未能生成二维码，正在自动切换为直连'
        }
      })
      emit({
        type: 'event', event: 'connection', accountId: state.accountId,
        data: { state: 'retrying', attempt }
      })
      try { socket.end(new Error('proxy_route_timeout')) } catch { }
      return
    }
    state.connection = 'retrying'
    emit({
      type: 'event', event: 'connection_issue', accountId: state.accountId,
      data: {
        code: 'qr_generation_timeout',
        recoverable: true,
        attempt,
        message: '二维码生成超时，程序正在自动重新建立连接'
      }
    })
    emit({
      type: 'event', event: 'connection', accountId: state.accountId,
      data: { state: 'retrying', attempt }
    })
    try { socket.end(new Error('qr_generation_timeout')) } catch { }
  }, QR_GENERATION_WATCHDOG_MS)

  socket.ev.on('creds.update', async () => {
    // Ignore credentials emitted by a socket that was superseded or manually
    // logged out. Without this guard a late Baileys callback can recreate the
    // cleared session and prevent the next launch from ever reaching QR pairing.
    if (state.connectionGeneration !== generation || state.socket !== socket || state.manualDisconnect) return
    try {
      await saveCreds()
    } catch (error) {
      emit({
        type: 'event', event: 'bridge_error', accountId: state.accountId,
        data: { code: 'credentials_save_failed', error: safeError(error) }
      })
    }
  })
  socket.ev.on('messages.upsert', update => {
    enqueueSync(async () => {
      for (const message of update.messages ?? []) await forwardMessage(message, update.type ?? 'notify')
    }, 'messages')
  })
  socket.ev.on('messaging-history.set', update => {
    enqueueSync(() => handleHistorySync(update), 'history')
  })
  socket.ev.on('messaging-history.status', update => {
    enqueueSync(async () => {
      emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: update.status === 'complete' ? 'complete' : 'paused', phase: syncTypeName(update.syncType), progress: update.explicit ? 100 : null, explicit: update.explicit, contacts: state.contacts.size, chats: state.chats.size, messages: state.historyTotals.messages } })
    }, 'history_status')
  })
  socket.ev.on('contacts.upsert', contacts => {
    enqueueSync(async () => {
      const items = await normalizeContacts(contacts, 'live')
      emitItems('contacts_upsert', items, 'live')
    }, 'contacts')
  })
  socket.ev.on('contacts.update', contacts => {
    enqueueSync(async () => {
      const items = await normalizeContacts(contacts, 'live_update')
      emitItems('contacts_upsert', items, 'live_update')
    }, 'contacts')
  })
  socket.ev.on('chats.upsert', chats => {
    enqueueSync(() => processChats(chats, 'live'), 'chats')
  })
  socket.ev.on('chats.update', chats => {
    enqueueSync(() => processChats(chats, 'live_update'), 'chats')
  })
  socket.ev.on('labels.edit', label => {
    if (!label || typeof label !== 'object') return
    const data = {
      id: String(label.id ?? ''),
      name: String(label.name ?? ''),
      color: Number(label.color ?? 0),
      deleted: Boolean(label.deleted),
      predefinedId: label.predefinedId != null ? Number(label.predefinedId) : null
    }
    if (!data.id) return
    emit({ type: 'event', event: 'label_upsert', accountId: state.accountId, data })
  })
  socket.ev.on('labels.association', payload => {
    const association = payload?.association
    if (!association) return
    const chatId = String(association.chatId ?? association.jid ?? '')
    const labelId = String(association.labelId ?? '')
    const type = payload.type === 'remove' ? 'remove' : 'add'
    if (!chatId || !labelId) return
    emit({
      type: 'event', event: 'chat_label_upsert', accountId: state.accountId,
      data: { chatId, labelId, type, phone: phoneFromJid(chatId) }
    })
  })
  socket.ev.on('lid-mapping.update', mapping => {
    enqueueSync(async () => {
      const lid = String(mapping?.lid ?? '')
      const jid = String(mapping?.pn ?? '')
      const phone = phoneFromJid(jid)
      if (!lid || !phone) return
      const contacts = [...state.contacts.values()].filter(item => item.jid === lid || item.sourceJid === lid)
      for (const item of contacts) rememberContact({ ...item, jid, phone, source: 'lid_mapping' })
      const chats = [...state.chats.values()].filter(item => item.jid === lid || item.sourceJid === lid)
      for (const item of chats) rememberChat({ ...item, jid, phone, source: 'lid_mapping' })
      emitItems('contacts_upsert', contacts.map(item => ({ ...item, jid, phone, source: 'lid_mapping' })), 'lid_mapping')
      emitItems('chats_upsert', chats.map(item => ({ ...item, jid, phone, source: 'lid_mapping' })), 'lid_mapping')
    }, 'lid_mapping')
  })
  socket.ev.on('messages.update', async updates => {
    for (const update of updates ?? []) {
      const jid = await resolveConversationJid(update.key)
      if (!shouldIngest(jid)) continue
      const providerMessageId = String(update.key?.id ?? '').trim()
      if (update.update?.message && providerMessageId) {
        const cached = state.messages.get(providerMessageId)
        await forwardMessage({
          ...(cached ?? {}),
          key: { ...(cached?.key ?? {}), ...(update.key ?? {}) },
          message: update.update.message,
          messageTimestamp: update.update.messageTimestamp ?? cached?.messageTimestamp ?? Math.floor(Date.now() / 1000)
        }, 'update')
      }
      const trackedTarget = state.outboundTargets.get(providerMessageId)
      if (trackedTarget) {
        const actualPhone = phoneFromJid(await phoneJidFromAnyJid(jid))
        if (!actualPhone || actualPhone !== trackedTarget.requestedPhone) {
          emit({
            type: 'event', event: 'message_status', accountId: state.accountId,
            data: {
              id: providerMessageId,
              jid: trackedTarget.requestedJid,
              status: 0,
              statusAt: new Date().toISOString(),
              deliveredAt: '',
              readAt: '',
              failureReason: 'whatsapp_target_mismatch',
              statusContext: 'target_verification',
              remoteJid: String(update.key?.remoteJid ?? ''),
              remoteJidAlt: String(update.key?.remoteJidAlt ?? '')
            }
          })
          continue
        }
      }
      const numericStatus = update.update?.status ?? null
      if (numericStatus == null) continue
      const failureDetails = [
        update.update?.error?.message,
        update.update?.error?.output?.payload?.message,
        ...(Array.isArray(update.update?.messageStubParameters) ? update.update.messageStubParameters : [])
      ].map(value => String(value ?? '').trim()).filter(Boolean)
      const failureReason = Number(numericStatus) === 0
        ? (failureDetails.length > 0 ? `WhatsApp 发送失败：${failureDetails.join('；')}` : 'WhatsApp 返回发送错误（账号可搜索或已保存联系人并不代表本次传输成功）')
        : ''
      emit({
        type: 'event', event: 'message_status', accountId: state.accountId,
        data: {
          id: update.key.id ?? '', jid, status: numericStatus,
          statusAt: new Date().toISOString(),
          deliveredAt: Number(numericStatus) >= 3 ? new Date().toISOString() : '',
          readAt: Number(numericStatus) >= 4 ? new Date().toISOString() : '',
          failureReason,
          statusContext: Object.keys(update.update ?? {}).sort().join(','),
          remoteJid: String(update.key?.remoteJid ?? ''),
          remoteJidAlt: String(update.key?.remoteJidAlt ?? '')
        }
      })
    }
  })
  socket.ev.on('message-receipt.update', async updates => {
    for (const update of updates ?? []) {
      const jid = await resolveDirectJid(update.key)
      if (!shouldForward(jid)) continue
      const providerMessageId = String(update.key?.id ?? '').trim()
      const trackedTarget = state.outboundTargets.get(providerMessageId)
      const expectedPhone = trackedTarget?.requestedPhone || phoneFromJid(await phoneJidFromAnyJid(jid))
      if (!expectedPhone || !await receiptBelongsToPhone(update.receipt, expectedPhone)) continue
      const deliveredAt = update.receipt?.receiptTimestamp == null ? '' : timestampToIso(update.receipt.receiptTimestamp)
      const readValue = update.receipt?.readTimestamp ?? update.receipt?.playedTimestamp
      const readAt = readValue == null ? '' : timestampToIso(readValue)
      const status = readAt ? 4 : deliveredAt ? 3 : null
      if (status == null) continue
      emit({
        type: 'event', event: 'message_status', accountId: state.accountId,
        data: {
          id: providerMessageId,
          jid: trackedTarget?.requestedJid || jid,
          status,
          statusAt: new Date().toISOString(),
          deliveredAt,
          readAt,
          failureReason: '',
          receiptUserJid: String(update.receipt?.userJid ?? ''),
          targetVerified: true
        }
      })
    }
  })
  socket.ev.on('connection.update', async update => {
    if (state.connectionGeneration !== generation || state.socket !== socket) return
    if (update.receivedPendingNotifications === true) state.pendingNotificationsAttempt = attempt
    if (update.qr) {
      state.qrSeen = true
      if (state.pairingTimer) clearTimeout(state.pairingTimer)
      state.pairingTimer = null
      try {
        const dataUrl = await QRCode.toDataURL(update.qr, { width: 320, margin: 2, errorCorrectionLevel: 'M' })
        emit({ type: 'event', event: 'qr', accountId: state.accountId, data: { dataUrl, attempt } })
      } catch (error) {
        state.qrSeen = false
        state.connection = 'retrying'
        emit({
          type: 'event', event: 'connection_issue', accountId: state.accountId,
          data: {
            code: 'qr_render_failed',
            recoverable: true,
            attempt,
            message: '已收到登录凭据，但二维码绘制失败，程序将自动重试',
            error: safeError(error)
          }
        })
        emit({
          type: 'event', event: 'connection', accountId: state.accountId,
          data: { state: 'retrying', attempt }
        })
        try { socket.end(new Error('qr_render_failed')) } catch { }
      }
    }
    if (update.connection === 'open') {
      if (state.pairingTimer) clearTimeout(state.pairingTimer)
      state.pairingTimer = null
      if (state.newDesktopPairing) {
        // The phone accepted the QR. Persist the registration immediately and
        // force the registered flag: Baileys can still report registered=false
        // at open time, and the later creds.update (registered=true) is dropped
        // by the generation guard once this socket is replaced, which made
        // every reconnect treat the session as brand-new and re-enter the
        // QR/upgrade loop.
        auth.creds.registered = true
        await saveCreds()
        // Stay on the paired web profile. WhatsApp rejects a Desktop-profile
        // reconnect of a web-paired session (status 428), so the old
        // desktop-history upgrade can never succeed on this protocol version.
        // Local history remains available from the database and new messages
        // sync live over the web session.
        state.desktopUpgradeRequested = false
        state.desktopUpgradeFailures = 0
      }
      state.connection = 'connected'
      if (state.desktopUpgradeRequested && !state.desktopHistoryProfile) {
        await writeDesktopHistoryProfile()
        state.desktopUpgradeRequested = false
      }
      const requiresHistoryRepair = state.existingSession && !state.desktopHistoryProfile
      emit({
        type: 'event', event: 'connection', accountId: state.accountId,
        data: {
          state: 'connected', user: socket.user?.id ?? '', name: socket.user?.name ?? '',
          existingSession: state.existingSession, desktopHistoryProfile: state.desktopHistoryProfile,
          requiresHistoryRepair
        }
      })
      if (requiresHistoryRepair) {
        emitDesktopHistoryRepairRequired(catchupSource)
        // NOTE: do not return here — web sessions still get group discovery
        // and on-demand history requests (5.18.15 fix); the repair hint is
        // informational only.
      }
      await getOfflineCatchupCoordinator().start({ socket, attempt, source: catchupSource, existingSession: state.existingSession })
      enqueueSync(() => discoverParticipatingGroups(`groups:${catchupSource}`), 'group_discovery')
      if (state.pendingNotificationsAttempt === attempt) {
        getOfflineCatchupCoordinator().receivePending({ socket, attempt })
      }
      return
    }
    if (update.receivedPendingNotifications === true) {
      getOfflineCatchupCoordinator().receivePending({ socket, attempt })
      return
    }
    if (update.connection !== 'close') return
    const statusCode = update.lastDisconnect?.error?.output?.statusCode
      ?? update.lastDisconnect?.error?.statusCode
      ?? null
    const loggedOut = statusCode === DisconnectReason.loggedOut
    if (state.pairingTimer) clearTimeout(state.pairingTimer)
    state.pairingTimer = null
    state.socket = null
    state.connection = loggedOut ? 'logged_out' : 'disconnected'
    if (loggedOut) {
      await resetSessionForQr()
    }
    emit({
      type: 'event', event: 'connection', accountId: state.accountId,
      data: { state: loggedOut ? 'logged_out' : 'disconnected', statusCode, error: safeError(update.lastDisconnect?.error) }
    })
    if (!loggedOut && !state.manualDisconnect) {
      // Persist the disconnect reason so repeated connection failures can be
      // diagnosed without a debug bridge build (pino is silent by default).
      try {
        const logPath = path.join(state.sessionDir, 'connection-errors.log')
        const currentSize = await fs.stat(logPath).then(s => s.size).catch(() => 0)
        if (currentSize < 512 * 1024) {
          const entry = JSON.stringify({
            at: new Date().toISOString(),
            attempt,
            statusCode,
            profile: state.desktopUpgradeRequested || state.desktopHistoryProfile ? 'desktop' : 'web',
            error: safeError(update.lastDisconnect?.error)
          })
          await fs.appendFile(logPath, `${entry}\n`, 'utf8')
        }
      } catch { }
      if (state.desktopUpgradeRequested) {
        // The Desktop-profile upgrade (full history) is failing; after a few
        // attempts give up the upgrade and fall back to the already-paired web
        // profile so the account still connects and live messages keep flowing.
        state.desktopUpgradeFailures += 1
        if (state.desktopUpgradeFailures >= DESKTOP_UPGRADE_MAX_FAILURES) {
          state.desktopUpgradeRequested = false
          state.desktopUpgradeFailures = 0
          emit({
            type: 'event', event: 'connection_issue', accountId: state.accountId,
            data: {
              code: 'desktop_history_upgrade_failed',
              recoverable: true,
              attempt,
              message: '完整历史同步暂不可用，已自动降级为普通连接模式；最新消息收发不受影响'
            }
          })
        }
      }
      // A recorded Desktop profile that the server now rejects (428) means the
      // session is actually a web pairing; clear the marker so reconnects use
      // the web profile instead of looping on the Desktop profile.
      if (state.desktopHistoryProfile && statusCode === DisconnectReason.connectionClosed) {
        state.desktopHistoryProfile = false
        try { await fs.rm(desktopHistoryProfilePath(), { force: true }) } catch { }
        emit({
          type: 'event', event: 'connection_issue', accountId: state.accountId,
          data: {
            code: 'desktop_profile_rejected',
            recoverable: true,
            attempt,
            message: 'WhatsApp 拒绝该会话的桌面完整历史模式，已自动切换为普通连接模式；最新消息收发不受影响'
          }
        })
      }
      if (state.currentProxyUrl && state.allowDirectFallback && !state.directFallbackUsed && !state.qrSeen) {
        state.directFallbackUsed = true
        state.currentProxyUrl = ''
        state.immediateReconnect = true
        emit({
          type: 'event', event: 'connection_issue', accountId: state.accountId,
          data: {
            code: 'proxy_route_failed',
            recoverable: true,
            attempt,
            message: 'Windows 系统代理在二维码生成前断开，正在自动切换为直连'
          }
        })
      }
      const reconnectImmediately = state.immediateReconnect
      state.immediateReconnect = false
      const retryDelay = reconnectImmediately ? 1000 : 5000
      emit({
        type: 'event', event: 'connection_stage', accountId: state.accountId,
        data: {
          state: 'retrying',
          attempt,
          message: reconnectImmediately
            ? '正在切换网络路线并重新连接'
            : '连接暂时中断，5 秒后自动重试'
        }
      })
      state.reconnectTimer = setTimeout(() => {
        if (state.connectionGeneration !== generation || state.manualDisconnect) return
        connect('reconnect').catch(error => emit({
          type: 'event', event: 'bridge_error', accountId: state.accountId,
          data: { error: safeError(error), code: 'automatic_reconnect_failed' }
        }))
      }, retryDelay)
    }
  })
}

async function handle(command) {
  const requestId = command.requestId ?? ''
  try {
    switch (command.command) {
      case 'ping':
        reply(requestId, true, { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.8.3', connection: state.connection })
        return
      case 'initialize': {
        state.accountId = validateAccountId(command.accountId ?? 'default')
        state.authKey = parseEncryptionKey(command.encryptionKey)
        state.sessionDir = resolveSessionDir(state.accountId)
        state.outboundTargets.clear()
        await fs.mkdir(state.sessionDir, { recursive: true })
        reply(requestId, true, { accountId: state.accountId, sessionDir: state.sessionDir })
        return
      }
      case 'connect':
        state.proxyUrl = normalizeProxyUrl(command.proxyUrl)
        state.currentProxyUrl = state.proxyUrl
        state.proxySource = String(command.proxySource ?? '')
        state.allowDirectFallback = Boolean(command.allowDirectFallback && state.proxyUrl)
        state.directFallbackUsed = false
        state.immediateReconnect = false
        await connect('startup')
        reply(requestId, true, { state: state.connection })
        return
      case 'validate_session': {
        if (!state.sessionDir || !state.authKey) throw new Error('bridge_not_initialized')
        const result = await loadAuthStateWithRecovery()
        reply(requestId, true, { recovered: result.recovered, recovery: result.recovery })
        return
      }
      case 'disconnect':
        state.manualDisconnect = true
        await closeSocket()
        emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'disconnected', manual: true } })
        reply(requestId, true, { state: state.connection })
        return
      case 'logout':
        state.manualDisconnect = true
        getOfflineCatchupCoordinator().cancel()
        state.connectionGeneration += 1
        if (state.reconnectTimer) clearTimeout(state.reconnectTimer)
        state.reconnectTimer = null
        if (state.pairingTimer) clearTimeout(state.pairingTimer)
        state.pairingTimer = null
        const logoutSocket = state.socket
        state.socket = null
        let remoteLogoutCompleted = false
        if (logoutSocket) {
          try {
            await Promise.race([
              logoutSocket.logout(),
              new Promise((_, reject) => setTimeout(() => reject(new Error('remote_logout_timeout')), 8000))
            ])
            remoteLogoutCompleted = true
          } catch (error) {
            emit({
              type: 'event', event: 'connection_issue', accountId: state.accountId,
              data: {
                code: 'remote_logout_incomplete',
                recoverable: true,
                message: '远端退出未确认，本机登录会话仍已清除，可立即重新扫码',
                error: safeError(error)
              }
            })
          }
          try { logoutSocket.end(new Error('waflow_logout')) } catch { }
        }
        state.outboundTargets.clear()
        await resetSessionForQr()
        state.connection = 'logged_out'
        emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'logged_out', manual: true } })
        reply(requestId, true, { state: 'logged_out', remoteLogoutCompleted })
        return
      case 'send_text': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const text = String(command.text ?? '').trim()
        if (!text || text.length > 4096) throw new Error('invalid_message_text')
        const requestedJid = await resolveOutboundJid(command.phone, command.jid)
        if (!shouldForward(requestedJid)) throw new Error('only_individual_contacts_supported')
        const fanout = await prepareOutboundDeviceFanout(requestedJid)
        const result = await state.socket.sendMessage(fanout.jid, { text }, quotedSendOptions(command, fanout.jid))
        const target = await requireVerifiedOutboundResult(result, command.phone, fanout.jid)
        rememberMessage(result)
        // sendMessage may return before WhatsApp has acknowledged the message.
        // Missing status means pending, never a confirmed send.
        reply(requestId, true, {
          id: target.providerMessageId,
          jid: target.requestedJid,
          timestamp: new Date().toISOString(),
          status: result?.status ?? 1,
          quotedMessageId: String(command.quotedMessageId ?? ''),
          senderDeviceSyncPrepared: fanout.senderDeviceSyncPrepared,
          senderDeviceCount: fanout.senderDeviceCount,
          recipientDeviceCount: fanout.recipientDeviceCount,
          ...target
        })
        return
      }
      case 'send_media': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const requestedJid = await resolveOutboundJid(command.phone, command.jid)
        if (!shouldForward(requestedJid)) throw new Error('only_individual_contacts_supported')
        const fanout = await prepareOutboundDeviceFanout(requestedJid)
        const media = await buildMediaMessage(command.path, command.caption)
        const result = await state.socket.sendMessage(fanout.jid, media.payload, quotedSendOptions(command, fanout.jid))
        const target = await requireVerifiedOutboundResult(result, command.phone, fanout.jid)
        rememberMessage(result)
        reply(requestId, true, {
          id: target.providerMessageId,
          jid: target.requestedJid,
          timestamp: new Date().toISOString(),
          status: result?.status ?? 1,
          kind: media.kind,
          mimeType: media.mimeType,
          fileName: media.fileName,
          quotedMessageId: String(command.quotedMessageId ?? ''),
          senderDeviceSyncPrepared: fanout.senderDeviceSyncPrepared,
          senderDeviceCount: fanout.senderDeviceCount,
          recipientDeviceCount: fanout.recipientDeviceCount,
          ...target
        })
        return
      }
      case 'validate_number': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const phone = String(command.phone ?? '').replace(/\D/g, '')
        if (phone.length < 7 || phone.length > 15) throw new Error('invalid_phone_number')
        const matches = await state.socket.onWhatsApp(phone)
        const match = (matches ?? []).find(item => item?.exists)
        reply(requestId, true, { phone, exists: Boolean(match), jid: match?.jid ?? '' })
        return
      }
      case 'revoke_message': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const jid = await resolveOutboundJid(command.phone, command.jid)
        if (!shouldForward(jid)) throw new Error('only_individual_contacts_supported')
        const id = String(command.messageId ?? '').trim()
        if (!id || id.length > 256) throw new Error('invalid_message_id')
        const result = await state.socket.sendMessage(jid, { delete: { remoteJid: jid, fromMe: true, id } })
        state.messages.delete(id)
        const timestamp = new Date().toISOString()
        emit({
          type: 'event', event: 'message_revoked', accountId: state.accountId,
          data: { id: result?.key?.id ?? '', jid, phone: phoneFromJid(jid), fromMe: true, revokedMessageId: id, isRevocation: true, timestamp, source: 'desktop' }
        })
        reply(requestId, true, { id: result?.key?.id ?? '', jid, revokedMessageId: id, timestamp })
        return
      }
      case 'set_chat_pin': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const jid = await resolveOutboundJid(command.phone, command.jid)
        if (!shouldForward(jid)) throw new Error('only_individual_contacts_supported')
        const pinned = Boolean(command.pinned)
        await state.socket.chatModify({ pin: pinned }, jid)
        const chat = state.chats.get(phoneFromJid(jid))
        if (chat) rememberChat({ ...chat, pinned, pinnedAt: pinned ? new Date().toISOString() : '' })
        reply(requestId, true, { jid, pinned })
        return
      }
      case 'create_group': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const subject = String(command.subject ?? '').trim()
        if (!subject || subject.length > 100) throw new Error('invalid_group_subject')
        if (!Array.isArray(command.participants)) throw new Error('invalid_group_participants')
        const ownPhone = phoneFromJid(String(state.socket.user?.id ?? ''))
        const participantJids = [...new Set(command.participants.map(jidFromPhone))]
          .filter(jid => phoneFromJid(jid) !== ownPhone)
        if (participantJids.length < 1 || participantJids.length > 256) throw new Error('invalid_group_participant_count')
        const result = await state.socket.groupCreate(subject, participantJids)
        const groupJid = String(result?.id ?? '')
        if (!groupJid.endsWith('@g.us')) throw new Error('group_create_missing_id')
        const participants = Array.isArray(result?.participants)
          ? result.participants.map(item => phoneFromJid(String(item?.id ?? item))).filter(Boolean)
          : participantJids.map(phoneFromJid)
        const data = { jid: groupJid, subject: String(result?.subject ?? subject), participantCount: participants.length, participants }
        emit({ type: 'event', event: 'group_created', accountId: state.accountId, data })
        reply(requestId, true, data)
        return
      }
      case 'sync_now': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        enqueueSync(manualSync, 'manual')
        reply(requestId, true, { state: 'started', existingSession: state.existingSession, contacts: state.contacts.size, chats: state.chats.size })
        return
      }
      case 'label_upsert': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const id = String(command.id ?? '').trim()
        if (!id) throw new Error('invalid_label_id')
        await state.socket.addLabel('status@broadcast', {
          id,
          name: String(command.name ?? ''),
          color: Number(command.color ?? 0),
          deleted: Boolean(command.deleted)
        })
        reply(requestId, true, { id })
        return
      }
      case 'chat_label_set': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const jid = await resolveOutboundJid(command.phone, command.jid)
        const labelId = String(command.labelId ?? '').trim()
        if (!labelId) throw new Error('invalid_label_id')
        const add = Boolean(command.add)
        if (add) await state.socket.addChatLabel(jid, labelId)
        else await state.socket.removeChatLabel(jid, labelId)
        const chat = state.chats.get(phoneFromJid(jid))
        if (chat) {
          const labels = new Set(chat.labels ?? [])
          if (add) labels.add(labelId)
          else labels.delete(labelId)
          rememberChat({ ...chat, labels: [...labels], source: 'live_update' })
        }
        reply(requestId, true, { jid, labelId, add })
        return
      }
      case 'catch_up_history': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const cursors = Array.isArray(command.cursors) ? command.cursors : []
        enqueueSync(() => catchUpOfflineMessages(cursors), 'offline_messages')
        reply(requestId, true, { state: 'started', existingSession: state.existingSession })
        return
      }
      default:
        throw new Error('unknown_command')
    }
  } catch (error) {
    reply(requestId, false, null, { code: safeError(error).split(':')[0], message: safeError(error) })
  }
}

const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
lines.on('line', line => {
  if (!line.trim()) return
  try {
    const command = JSON.parse(line)
    if (['connect', 'disconnect', 'logout'].includes(command.command)) {
      state.lifecycleQueue = state.lifecycleQueue.then(
        () => handle(command),
        () => handle(command))
      return
    }
    handle(command)
  }
  catch (error) { emit({ type: 'event', event: 'bridge_error', data: { error: safeError(error) } }) }
})
lines.on('close', async () => {
  state.manualDisconnect = true
  await closeSocket()
  process.exit(0)
})

emit({ type: 'event', event: 'ready', data: { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.8.3' } })
