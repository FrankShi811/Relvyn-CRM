// SPDX-License-Identifier: GPL-3.0-only
// Account-level send governor.
//
// Every outbound message — human, AI auto-reply, campaign — passes through one
// governor instance per bridge process (one process per WhatsApp account).
// Before this module, `send_text` and `send_media` called Baileys directly with
// no rate limit, no jitter and no daily cap; only the campaign scheduler in the
// desktop app throttled itself, and it did so on a strictly even grid, which is
// itself an automation fingerprint.
//
// Design rules:
//   - Fail closed. When the governor cannot prove a send is within budget it
//     refuses; it never falls through to "send anyway".
//   - Counters persist. A process restart must not reset the daily cap,
//     otherwise restarting the app is a trivial way to bypass it.
//   - Jitter is mandatory. Evenly spaced sends are the pattern being avoided.
//   - AI has a sub-quota. Exhausting automated replies must never block a human
//     from answering a customer.

export const DEFAULT_OUTBOUND_CONFIG = Object.freeze({
  enabled: true,
  minGapMs: 3000,          // floor between two sends
  jitterMs: 4000,          // uniform extra delay in [0, jitterMs)
  burstCapacity: 5,        // token bucket size
  refillPerMinute: 12,     // tokens added per minute
  hourlyCap: 120,
  dailyCap: 400,
  aiDailyCapRatio: 0.5,    // ai_auto share of dailyCap
  newAccountWarmupDays: 7,
  warmupDailyCaps: [40, 60, 90, 130, 180, 250, 400],
  // Must stay comfortably below the desktop app's 45s RPC timeout
  // (WhatsAppBridgeClient.SendCommandAsync). Queueing past that timeout would
  // leave C# believing the send failed while the bridge still sends it — the
  // exact duplicate-send window this module exists to close. Anything longer is
  // refused with a retryAfterMs hint so the caller reschedules instead.
  maxQueueWaitMs: 30000,
  maxQueueDepth: 200
})

export const ORIGINS = Object.freeze(['human', 'ai_auto', 'campaign'])

// Errors the caller is expected to surface verbatim to the desktop app.
export class OutboundBlockedError extends Error {
  constructor(code, detail = {}) {
    super(code)
    this.name = 'OutboundBlockedError'
    this.code = code
    this.detail = detail
  }
}

function startOfLocalDay(ms) {
  const d = new Date(ms)
  d.setHours(0, 0, 0, 0)
  return d.getTime()
}

function normalizeOrigin(origin) {
  const value = String(origin ?? 'human')
  return ORIGINS.includes(value) ? value : 'human'
}

function clampNumber(value, min, max, fallback) {
  const n = Number(value)
  if (!Number.isFinite(n)) return fallback
  return Math.min(max, Math.max(min, n))
}

export class OutboundGovernor {
  /**
   * @param {object} options
   * @param {object} [options.config]      partial override of DEFAULT_OUTBOUND_CONFIG
   * @param {() => number} [options.now]   injectable clock (ms)
   * @param {(ms:number)=>Promise<void>} [options.sleep]
   * @param {() => number} [options.random] injectable [0,1) for jitter
   * @param {(state:object)=>Promise<void>|void} [options.persist] durable counter writer
   * @param {object} [options.restored]    previously persisted counters
   */
  constructor(options = {}) {
    this.config = { ...DEFAULT_OUTBOUND_CONFIG, ...(options.config ?? {}) }
    this._now = options.now ?? (() => Date.now())
    this._sleep = options.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)))
    this._random = options.random ?? Math.random
    this._persist = options.persist ?? (() => {})

    const now = this._now()
    const restored = options.restored ?? {}

    this.firstPairedAt = Number(restored.firstPairedAt) || 0
    this.dayStart = Number(restored.dayStart) || startOfLocalDay(now)
    this.dailyCounts = { human: 0, ai_auto: 0, campaign: 0, ...(restored.dailyCounts ?? {}) }
    // Sliding one-hour window of send timestamps.
    this.recentSends = Array.isArray(restored.recentSends) ? restored.recentSends.slice(-1000) : []

    this.tokens = this.config.burstCapacity
    this.lastRefillAt = now
    this.lastSentAt = Number(restored.lastSentAt) || 0
    this.nextEligibleAt = 0

    this.suspendedUntil = 0
    this.suspendReason = ''
    this.suspendIndefinite = false

    this.queueDepth = 0
    this._chain = Promise.resolve()
  }

  configure(partial = {}) {
    const c = this.config
    const next = { ...c, ...partial }
    next.minGapMs = clampNumber(next.minGapMs, 0, 300000, c.minGapMs)
    next.jitterMs = clampNumber(next.jitterMs, 0, 300000, c.jitterMs)
    next.burstCapacity = clampNumber(next.burstCapacity, 1, 100, c.burstCapacity)
    next.refillPerMinute = clampNumber(next.refillPerMinute, 1, 600, c.refillPerMinute)
    next.hourlyCap = clampNumber(next.hourlyCap, 1, 10000, c.hourlyCap)
    next.dailyCap = clampNumber(next.dailyCap, 1, 20000, c.dailyCap)
    next.aiDailyCapRatio = clampNumber(next.aiDailyCapRatio, 0, 1, c.aiDailyCapRatio)
    next.newAccountWarmupDays = clampNumber(next.newAccountWarmupDays, 0, 60, c.newAccountWarmupDays)
    next.maxQueueWaitMs = clampNumber(next.maxQueueWaitMs, 0, 3600000, c.maxQueueWaitMs)
    next.maxQueueDepth = clampNumber(next.maxQueueDepth, 1, 10000, c.maxQueueDepth)
    if (!Array.isArray(next.warmupDailyCaps) || next.warmupDailyCaps.length === 0)
      next.warmupDailyCaps = c.warmupDailyCaps
    this.config = next
    this.tokens = Math.min(this.tokens, next.burstCapacity)
    return this.config
  }

  /** Record when this account was first paired, so warm-up caps can apply. */
  markPaired(atMs) {
    const at = Number(atMs) || this._now()
    if (!this.firstPairedAt || at < this.firstPairedAt) {
      this.firstPairedAt = at
      this._save()
    }
  }

  /** Effective daily cap, taking new-account warm-up into account. */
  effectiveDailyCap(now = this._now()) {
    const { dailyCap, newAccountWarmupDays, warmupDailyCaps } = this.config
    if (!this.firstPairedAt || newAccountWarmupDays <= 0) return dailyCap
    const ageDays = Math.floor((now - this.firstPairedAt) / 86400000)
    if (ageDays >= newAccountWarmupDays) return dailyCap
    const step = warmupDailyCaps[Math.min(ageDays, warmupDailyCaps.length - 1)]
    return Math.min(dailyCap, Number(step) || dailyCap)
  }

  effectiveAiDailyCap(now = this._now()) {
    return Math.max(1, Math.floor(this.effectiveDailyCap(now) * this.config.aiDailyCapRatio))
  }

  /**
   * Suspend all sending. Called on HTTP 429 (temporary) or 403 / suspected
   * account restriction (indefinite, needs human acknowledgement).
   */
  suspend(reason, { untilMs = 0, indefinite = false } = {}) {
    this.suspendReason = String(reason ?? 'unknown')
    this.suspendIndefinite = Boolean(indefinite)
    this.suspendedUntil = indefinite ? Number.MAX_SAFE_INTEGER : Number(untilMs) || 0
    this._save()
  }

  resume() {
    this.suspendedUntil = 0
    this.suspendReason = ''
    this.suspendIndefinite = false
    this._save()
  }

  isSuspended(now = this._now()) {
    return this.suspendIndefinite || now < this.suspendedUntil
  }

  _rollDay(now) {
    const today = startOfLocalDay(now)
    if (today !== this.dayStart) {
      this.dayStart = today
      this.dailyCounts = { human: 0, ai_auto: 0, campaign: 0 }
    }
  }

  _pruneHour(now) {
    const cutoff = now - 3600000
    if (this.recentSends.length && this.recentSends[0] <= cutoff)
      this.recentSends = this.recentSends.filter(ts => ts > cutoff)
  }

  _refill(now) {
    const elapsed = Math.max(0, now - this.lastRefillAt)
    if (elapsed <= 0) return
    const gained = (elapsed / 60000) * this.config.refillPerMinute
    this.tokens = Math.min(this.config.burstCapacity, this.tokens + gained)
    this.lastRefillAt = now
  }

  dailyTotal() {
    return ORIGINS.reduce((sum, key) => sum + (Number(this.dailyCounts[key]) || 0), 0)
  }

  /**
   * Reserve a send slot. Resolves with { waitedMs } once the caller may send.
   * Throws OutboundBlockedError when the send must not happen at all.
   *
   * Calls are serialized so the minimum gap is enforced across concurrent
   * callers rather than per-caller.
   */
  async acquire({ origin = 'human', signal } = {}) {
    if (!this.config.enabled) return { waitedMs: 0, bypassed: true }
    if (this.queueDepth >= this.config.maxQueueDepth)
      throw new OutboundBlockedError('outbound_queue_full', { queueDepth: this.queueDepth })

    this.queueDepth += 1
    const run = this._chain.then(() => this._acquireSerial(normalizeOrigin(origin), signal))
    // Keep the chain alive even when one acquire rejects.
    this._chain = run.then(() => {}, () => {})
    try {
      return await run
    } finally {
      this.queueDepth -= 1
    }
  }

  async _acquireSerial(origin, signal) {
    const startedAt = this._now()

    for (;;) {
      if (signal?.aborted) throw new OutboundBlockedError('outbound_aborted')

      const now = this._now()
      this._rollDay(now)
      this._pruneHour(now)
      this._refill(now)

      // --- hard blocks: never queue, tell the user ---------------------------
      if (this.isSuspended(now)) {
        throw new OutboundBlockedError(
          this.suspendIndefinite ? 'outbound_suspended_account_risk' : 'outbound_suspended_rate_limited',
          { reason: this.suspendReason, until: this.suspendedUntil }
        )
      }

      const dailyCap = this.effectiveDailyCap(now)
      if (this.dailyTotal() >= dailyCap)
        throw new OutboundBlockedError('outbound_daily_cap_reached', { dailyCap, sent: this.dailyTotal() })

      if (origin === 'ai_auto') {
        const aiCap = this.effectiveAiDailyCap(now)
        if ((this.dailyCounts.ai_auto ?? 0) >= aiCap)
          throw new OutboundBlockedError('outbound_ai_daily_cap_reached', { aiCap, sent: this.dailyCounts.ai_auto })
      }

      // --- soft waits: queue, but only briefly -------------------------------
      // Each candidate carries the reason so a refusal can name the real cause
      // instead of a generic "too long".
      const waits = []

      if (this.recentSends.length >= this.config.hourlyCap) {
        // Oldest send in the window must age out before another may go.
        waits.push({ ms: this.recentSends[0] + 3600000 - now, code: 'outbound_hourly_cap_reached' })
      }

      if (this.tokens < 1) {
        const deficit = 1 - this.tokens
        waits.push({ ms: (deficit / this.config.refillPerMinute) * 60000, code: 'outbound_burst_exhausted' })
      }

      if (this.nextEligibleAt > now)
        waits.push({ ms: this.nextEligibleAt - now, code: 'outbound_min_gap' })

      const blocking = waits.reduce(
        (worst, item) => (item.ms > worst.ms ? item : worst),
        { ms: 0, code: 'outbound_min_gap' }
      )
      const wait = Math.ceil(blocking.ms)

      if (wait <= 0) {
        this.tokens -= 1
        return { waitedMs: this._now() - startedAt, origin }
      }

      const elapsed = this._now() - startedAt
      if (elapsed + wait > this.config.maxQueueWaitMs) {
        throw new OutboundBlockedError(blocking.code, {
          retryAfterMs: wait,
          waitedMs: elapsed,
          maxQueueWaitMs: this.config.maxQueueWaitMs,
          hourlyCap: this.config.hourlyCap,
          hourlyCount: this.recentSends.length
        })
      }

      await this._sleep(wait)
    }
  }

  /**
   * Confirm a send actually happened. Only this advances the counters — a call
   * that acquired a slot but then failed to send does not consume quota,
   * except for the token already deducted (deliberate: a failed send still hit
   * WhatsApp's servers).
   */
  recordSent({ origin = 'human', at } = {}) {
    const now = Number(at) || this._now()
    const key = normalizeOrigin(origin)
    this._rollDay(now)
    this._pruneHour(now)
    this.dailyCounts[key] = (Number(this.dailyCounts[key]) || 0) + 1
    this.recentSends.push(now)
    this.lastSentAt = now
    const jitter = Math.floor(this._random() * this.config.jitterMs)
    this.nextEligibleAt = now + this.config.minGapMs + jitter
    this._save()
    return { nextEligibleAt: this.nextEligibleAt, jitter }
  }

  snapshot(now = this._now()) {
    this._rollDay(now)
    this._pruneHour(now)
    return {
      enabled: this.config.enabled,
      dayStart: this.dayStart,
      dailyCounts: { ...this.dailyCounts },
      dailyTotal: this.dailyTotal(),
      dailyCap: this.effectiveDailyCap(now),
      aiDailyCap: this.effectiveAiDailyCap(now),
      hourlyCount: this.recentSends.length,
      hourlyCap: this.config.hourlyCap,
      tokens: Number(this.tokens.toFixed(3)),
      queueDepth: this.queueDepth,
      lastSentAt: this.lastSentAt,
      nextEligibleAt: this.nextEligibleAt,
      suspended: this.isSuspended(now),
      suspendReason: this.suspendReason,
      suspendIndefinite: this.suspendIndefinite,
      suspendedUntil: this.suspendedUntil,
      firstPairedAt: this.firstPairedAt,
      warmupActive: Boolean(this.firstPairedAt) &&
        (now - this.firstPairedAt) < this.config.newAccountWarmupDays * 86400000
    }
  }

  /** Serializable subset for disk persistence. */
  persistedState() {
    return {
      firstPairedAt: this.firstPairedAt,
      dayStart: this.dayStart,
      dailyCounts: { ...this.dailyCounts },
      recentSends: this.recentSends.slice(-1000),
      lastSentAt: this.lastSentAt
    }
  }

  _save() {
    try {
      const result = this._persist(this.persistedState())
      if (result && typeof result.catch === 'function') result.catch(() => {})
    } catch {
      // Persistence is best effort; losing counters must never block a send
      // decision that has already been made.
    }
  }
}

/**
 * Map a disconnect status code to a governor action.
 * Returns null when the code needs no send-side reaction.
 */
export function suspensionForStatusCode(statusCode, { now = Date.now(), consecutive = 1 } = {}) {
  const code = Number(statusCode)
  if (code === 429) {
    // 5s, 30s, 2m, 10m, 30m — capped.
    const ladder = [5000, 30000, 120000, 600000, 1800000]
    const step = ladder[Math.min(Math.max(consecutive, 1) - 1, ladder.length - 1)]
    return {
      reason: 'whatsapp_rate_limited',
      untilMs: now + step,
      indefinite: false,
      retryDelayMs: step,
      severity: 'warning'
    }
  }
  // 401 / loggedOut is handled by the pairing reset path, not here.
  if (code === 401) return null
  if (code >= 402 && code < 500) {
    return {
      reason: `whatsapp_client_error_${code}`,
      untilMs: 0,
      indefinite: true,
      retryDelayMs: 0,
      severity: 'critical'
    }
  }
  return null
}
