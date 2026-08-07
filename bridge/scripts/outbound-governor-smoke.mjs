// SPDX-License-Identifier: GPL-3.0-only
import assert from 'node:assert/strict'
import {
  OutboundGovernor,
  OutboundBlockedError,
  suspensionForStatusCode,
  DEFAULT_OUTBOUND_CONFIG
} from '../src/outbound-governor.mjs'

// A virtual clock: sleep() jumps time forward instead of waiting, so the whole
// suite runs in milliseconds while exercising the real waiting arithmetic.
function harness(overrides = {}, restored = undefined) {
  let now = Date.parse('2026-08-07T09:00:00.000Z')
  const saved = []
  let randomValue = 0.5
  const governor = new OutboundGovernor({
    config: { minGapMs: 1000, jitterMs: 1000, ...overrides },
    now: () => now,
    sleep: async ms => { now += ms },
    random: () => randomValue,
    persist: state => { saved.push(state) },
    restored
  })
  return {
    governor,
    saved,
    advance: ms => { now += ms },
    at: () => now,
    setRandom: v => { randomValue = v }
  }
}

async function expectBlocked(promise, code) {
  try {
    await promise
    assert.fail(`expected OutboundBlockedError(${code})`)
  } catch (error) {
    assert.ok(error instanceof OutboundBlockedError, `expected OutboundBlockedError, got ${error}`)
    assert.equal(error.code, code)
    return error
  }
}

// --- 1. minimum gap + jitter are enforced between sends ----------------------
{
  const { governor, at, setRandom } = harness()
  setRandom(0)                    // jitter 0 -> gap is exactly minGapMs
  await governor.acquire({ origin: 'human' })
  const first = at()
  governor.recordSent({ origin: 'human' })
  await governor.acquire({ origin: 'human' })
  const second = at()
  assert.equal(second - first, 1000, 'second send must wait the full minimum gap')
}

// --- 2. jitter actually varies the spacing (no even grid) --------------------
{
  const gaps = []
  for (const r of [0, 0.25, 0.5, 0.75, 0.99]) {
    const { governor, at, setRandom } = harness()
    setRandom(r)
    await governor.acquire({ origin: 'ai_auto' })
    const first = at()
    governor.recordSent({ origin: 'ai_auto' })
    await governor.acquire({ origin: 'ai_auto' })
    gaps.push(at() - first)
  }
  assert.equal(new Set(gaps).size, gaps.length, 'jitter must produce distinct gaps')
  assert.ok(Math.min(...gaps) >= 1000, 'never shorter than minGapMs')
  assert.ok(Math.max(...gaps) < 2000, 'never longer than minGapMs + jitterMs')
}

// --- 3. token bucket limits a burst ------------------------------------------
{
  const { governor, at } = harness({
    minGapMs: 0, jitterMs: 0, burstCapacity: 3, refillPerMinute: 60 // 1/s
  })
  const start = at()
  for (let i = 0; i < 3; i += 1) {
    await governor.acquire({ origin: 'human' })
    governor.recordSent({ origin: 'human' })
  }
  assert.equal(at() - start, 0, 'burst of 3 goes out immediately')
  await governor.acquire({ origin: 'human' })
  assert.ok(at() - start >= 1000, '4th send waits for a refilled token')
}

// --- 4. hourly cap refuses with retryAfterMs rather than parking the RPC -----
// Queueing here would outlive the desktop app's 45s RPC timeout and reopen the
// duplicate-send window, so the governor must refuse and hand back a hint.
{
  const { governor, advance } = harness({
    minGapMs: 0, jitterMs: 0, burstCapacity: 100, refillPerMinute: 600, hourlyCap: 3
  })
  for (let i = 0; i < 3; i += 1) {
    await governor.acquire({ origin: 'human' })
    governor.recordSent({ origin: 'human' })
  }
  const error = await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_hourly_cap_reached')
  assert.ok(error.detail.retryAfterMs > 0, 'refusal must tell the caller when to retry')
  assert.equal(error.detail.hourlyCount, 3)
  advance(error.detail.retryAfterMs + 1)
  assert.ok(await governor.acquire({ origin: 'human' }), 'retrying after the hint succeeds')
}

// --- 4b. maxQueueWaitMs must stay under the 45s RPC timeout ------------------
{
  assert.ok(
    DEFAULT_OUTBOUND_CONFIG.maxQueueWaitMs < 45000,
    'default queue ceiling must stay below WhatsAppBridgeClient 45s RPC timeout'
  )
}

// --- 5. daily cap is a hard block, not a queue -------------------------------
{
  const { governor } = harness({ minGapMs: 0, jitterMs: 0, dailyCap: 2, burstCapacity: 10 })
  for (let i = 0; i < 2; i += 1) {
    await governor.acquire({ origin: 'human' })
    governor.recordSent({ origin: 'human' })
  }
  const error = await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_daily_cap_reached')
  assert.equal(error.detail.dailyCap, 2)
}

// --- 6. AI sub-quota exhausts before human quota -----------------------------
{
  const { governor } = harness({
    minGapMs: 0, jitterMs: 0, dailyCap: 10, aiDailyCapRatio: 0.2, burstCapacity: 50
  })
  for (let i = 0; i < 2; i += 1) {           // aiDailyCap = floor(10 * 0.2) = 2
    await governor.acquire({ origin: 'ai_auto' })
    governor.recordSent({ origin: 'ai_auto' })
  }
  await expectBlocked(governor.acquire({ origin: 'ai_auto' }), 'outbound_ai_daily_cap_reached')
  // A human must still be able to reply to the same customer.
  const ok = await governor.acquire({ origin: 'human' })
  assert.ok(ok, 'human sending must survive AI quota exhaustion')
}

// --- 7. suspension (429 / 403) blocks everything -----------------------------
{
  const { governor, advance } = harness({ minGapMs: 0, jitterMs: 0 })
  governor.suspend('whatsapp_rate_limited', { untilMs: governor._now() + 30000 })
  await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_suspended_rate_limited')
  advance(31000)
  assert.ok(await governor.acquire({ origin: 'human' }), 'temporary suspension lifts on its own')

  governor.suspend('whatsapp_client_error_403', { indefinite: true })
  await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_suspended_account_risk')
  advance(86400000 * 30)
  await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_suspended_account_risk')
  governor.resume()
  assert.ok(await governor.acquire({ origin: 'human' }), 'indefinite suspension needs explicit resume')
}

// --- 8. new-account warm-up caps ---------------------------------------------
{
  const { governor, advance } = harness({ dailyCap: 400, newAccountWarmupDays: 7 })
  governor.markPaired(governor._now())
  assert.equal(governor.effectiveDailyCap(), 40, 'day 0 warm-up cap')
  advance(86400000)
  assert.equal(governor.effectiveDailyCap(), 60, 'day 1 warm-up cap')
  advance(86400000 * 6)
  assert.equal(governor.effectiveDailyCap(), 400, 'warm-up ends after 7 days')
}

// --- 9. counters survive a restart (restart must not reset the daily cap) ----
{
  const first = harness({ minGapMs: 0, jitterMs: 0, dailyCap: 3, burstCapacity: 10 })
  for (let i = 0; i < 3; i += 1) {
    await first.governor.acquire({ origin: 'human' })
    first.governor.recordSent({ origin: 'human' })
  }
  const persisted = first.saved.at(-1)
  assert.equal(persisted.dailyCounts.human, 3)

  const restarted = harness({ minGapMs: 0, jitterMs: 0, dailyCap: 3, burstCapacity: 10 }, persisted)
  await expectBlocked(restarted.governor.acquire({ origin: 'human' }), 'outbound_daily_cap_reached')
}

// --- 10. day rollover resets counters ----------------------------------------
{
  const { governor, advance } = harness({ minGapMs: 0, jitterMs: 0, dailyCap: 1, burstCapacity: 10 })
  await governor.acquire({ origin: 'human' })
  governor.recordSent({ origin: 'human' })
  await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_daily_cap_reached')
  advance(86400000)
  assert.ok(await governor.acquire({ origin: 'human' }), 'a new local day restores quota')
}

// --- 11. concurrent callers are serialized, gap applies across all -----------
{
  const { governor, at } = harness({ minGapMs: 500, jitterMs: 0, burstCapacity: 10, refillPerMinute: 600 })
  const start = at()
  const order = []
  await Promise.all([1, 2, 3].map(async n => {
    await governor.acquire({ origin: 'ai_auto' })
    order.push(n)
    governor.recordSent({ origin: 'ai_auto' })
  }))
  assert.deepEqual(order, [1, 2, 3], 'FIFO across concurrent callers')
  assert.ok(at() - start >= 1000, 'gap is global, not per-caller')
}

// --- 12. queue-wait ceiling refuses instead of hanging -----------------------
{
  const { governor } = harness({
    minGapMs: 0, jitterMs: 0, burstCapacity: 1, refillPerMinute: 1, maxQueueWaitMs: 5000
  })
  await governor.acquire({ origin: 'human' })
  governor.recordSent({ origin: 'human' })
  // next token needs 60s, ceiling is 5s
  const error = await expectBlocked(governor.acquire({ origin: 'human' }), 'outbound_burst_exhausted')
  assert.ok(error.detail.retryAfterMs >= 55000, 'refusal carries the real wait')
  assert.equal(error.detail.maxQueueWaitMs, 5000)
}

// --- 13. disabled governor is a pass-through (kill switch) -------------------
{
  const { governor, at } = harness({ enabled: false, minGapMs: 60000 })
  const start = at()
  const result = await governor.acquire({ origin: 'ai_auto' })
  assert.equal(result.bypassed, true)
  assert.equal(at() - start, 0)
}

// --- 14. status-code mapping --------------------------------------------------
{
  const now = 1_000_000
  const first = suspensionForStatusCode(429, { now, consecutive: 1 })
  const third = suspensionForStatusCode(429, { now, consecutive: 3 })
  assert.equal(first.indefinite, false)
  assert.equal(first.retryDelayMs, 5000)
  assert.equal(third.retryDelayMs, 120000, '429 backoff is exponential, not a flat 5s')

  const forbidden = suspensionForStatusCode(403, { now })
  assert.equal(forbidden.indefinite, true, '403 must not auto-resume')
  assert.equal(forbidden.severity, 'critical')

  assert.equal(suspensionForStatusCode(401, { now }), null, '401 is the pairing-reset path')
  assert.equal(suspensionForStatusCode(500, { now }), null, 'server errors are a reconnect concern, not a send block')
  assert.equal(suspensionForStatusCode(null, { now }), null)
}

// --- 15. config validation clamps hostile input ------------------------------
{
  const { governor } = harness()
  const applied = governor.configure({
    minGapMs: -5, dailyCap: 999999, aiDailyCapRatio: 7, refillPerMinute: 'abc', warmupDailyCaps: []
  })
  assert.equal(applied.minGapMs, 0)
  assert.equal(applied.dailyCap, 20000)
  assert.equal(applied.aiDailyCapRatio, 1)
  assert.equal(applied.refillPerMinute, DEFAULT_OUTBOUND_CONFIG.refillPerMinute)
  assert.ok(applied.warmupDailyCaps.length > 0, 'empty warm-up ladder falls back to default')
}

// --- 16. snapshot reports what the UI needs ----------------------------------
{
  const { governor } = harness({ minGapMs: 0, jitterMs: 0, dailyCap: 100 })
  await governor.acquire({ origin: 'ai_auto' })
  governor.recordSent({ origin: 'ai_auto' })
  const snap = governor.snapshot()
  assert.equal(snap.dailyCounts.ai_auto, 1)
  assert.equal(snap.dailyTotal, 1)
  assert.equal(snap.dailyCap, 100)
  assert.equal(snap.hourlyCount, 1)
  assert.equal(snap.suspended, false)
  assert.ok(snap.nextEligibleAt > 0)
}

console.log('outbound-governor smoke: 16 scenarios passed')
