// SPDX-License-Identifier: GPL-3.0-only
// Replay protection for outbound sends.
//
// The desktop app gives every RPC 45 seconds (WhatsAppBridgeClient.SendCommandAsync).
// When that timeout fires the C# side treats the send as failed and may retry,
// but WhatsApp may well have accepted the original message — the campaign
// scheduler's own retry path (CampaignAutomationService) has exactly this
// window. Keying a send on an idempotency key lets the bridge return the first
// result instead of producing a second message.
//
// Deliberately in-memory: a bridge restart also drops the socket, so any
// in-flight RPC has already failed on the C# side and will be re-driven from
// persisted state rather than replayed.

export const DEFAULT_IDEMPOTENCY_TTL_MS = 10 * 60 * 1000
export const DEFAULT_IDEMPOTENCY_MAX_ENTRIES = 1000

export class IdempotencyStore {
  constructor({
    ttlMs = DEFAULT_IDEMPOTENCY_TTL_MS,
    maxEntries = DEFAULT_IDEMPOTENCY_MAX_ENTRIES,
    now = () => Date.now()
  } = {}) {
    this.ttlMs = ttlMs
    this.maxEntries = maxEntries
    this._now = now
    // Insertion-ordered, so the first key is always the oldest.
    this._entries = new Map()
  }

  get size() {
    return this._entries.size
  }

  /** Drop expired entries, then evict oldest-first down to maxEntries. */
  prune(now = this._now()) {
    for (const [key, entry] of this._entries) {
      if (now - entry.at > this.ttlMs) this._entries.delete(key)
    }
    while (this._entries.size > this.maxEntries) {
      const oldest = this._entries.keys().next().value
      if (oldest === undefined) break
      this._entries.delete(oldest)
    }
    return this
  }

  /**
   * Returns the stored result for a key, or null. An expired entry is treated
   * as absent (and removed) so a stale key never suppresses a real send.
   */
  lookup(key) {
    const normalized = normalizeKey(key)
    if (!normalized) return null
    const entry = this._entries.get(normalized)
    if (!entry) return null
    if (this._now() - entry.at > this.ttlMs) {
      this._entries.delete(normalized)
      return null
    }
    return entry.result
  }

  /** Record a completed send. Re-recording a key refreshes its position. */
  remember(key, result) {
    const normalized = normalizeKey(key)
    if (!normalized) return result
    this._entries.delete(normalized)
    this._entries.set(normalized, { result, at: this._now() })
    this.prune()
    return result
  }

  clear() {
    this._entries.clear()
    return this
  }
}

export function normalizeKey(value) {
  const key = String(value ?? '').trim()
  return key.length > 0 && key.length <= 200 ? key : ''
}

/**
 * Decide whether an outbound command may proceed.
 *
 * Pure with respect to the caller's state: everything it needs is passed in,
 * so the ordering rules below are testable without a WhatsApp socket.
 *
 * Order matters:
 *   1. connection    — never queue against a dead socket
 *   2. catch-up      — the conversation view is still incomplete
 *   3. idempotency   — a replay must not consume send budget
 *   4. governor      — only now spend from the account's budget
 */
export async function planOutboundSend({
  connection,
  catchUpActive,
  idempotency,
  governor,
  command = {}
}) {
  if (connection !== 'connected') throw new Error('whatsapp_not_connected')
  if (catchUpActive) throw new Error('catchup_in_progress')

  const key = normalizeKey(command.idempotencyKey)
  if (key) {
    const cached = idempotency?.lookup(key)
    if (cached) return { replayed: { ...cached, idempotentReplay: true } }
  }

  if (!governor) throw new Error('bridge_not_initialized')
  const origin = String(command.origin ?? 'human')
  const reservation = await governor.acquire({ origin })
  return { origin, key, waitedMs: reservation.waitedMs ?? 0 }
}

/** Commit a successful send: charge the budget and store the replay result. */
export function commitOutboundSend({ slot, result, idempotency, governor }) {
  if (!slot || slot.replayed) return result
  governor?.recordSent({ origin: slot.origin })
  if (slot.key) idempotency?.remember(slot.key, result)
  return result
}
