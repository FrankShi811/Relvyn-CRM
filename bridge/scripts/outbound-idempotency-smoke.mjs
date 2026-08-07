// SPDX-License-Identifier: GPL-3.0-only
import assert from 'node:assert/strict'
import {
  IdempotencyStore,
  planOutboundSend,
  commitOutboundSend,
  normalizeKey
} from '../src/outbound-idempotency.mjs'
import { OutboundGovernor, OutboundBlockedError } from '../src/outbound-governor.mjs'

function clock(start = 1_000_000) {
  let t = start
  return { now: () => t, advance: ms => { t += ms } }
}

function governorAt(c, config = {}) {
  return new OutboundGovernor({
    config: { minGapMs: 0, jitterMs: 0, ...config },
    now: c.now,
    sleep: async ms => c.advance(ms),
    random: () => 0
  })
}

async function expectThrows(promise, message) {
  try {
    await promise
    assert.fail(`expected throw: ${message}`)
  } catch (error) {
    return error
  }
}

// --- key normalization --------------------------------------------------------
{
  assert.equal(normalizeKey('  abc  '), 'abc')
  assert.equal(normalizeKey(''), '')
  assert.equal(normalizeKey(null), '')
  assert.equal(normalizeKey(undefined), '')
  assert.equal(normalizeKey('x'.repeat(201)), '', 'absurd keys are rejected, not stored')
  assert.equal(normalizeKey('x'.repeat(200)).length, 200)
}

// --- store: TTL expiry --------------------------------------------------------
{
  const c = clock()
  const store = new IdempotencyStore({ ttlMs: 1000, now: c.now })
  store.remember('k1', { id: 'msg-1' })
  assert.deepEqual(store.lookup('k1'), { id: 'msg-1' })
  c.advance(1001)
  assert.equal(store.lookup('k1'), null, 'expired entry must not suppress a real send')
  assert.equal(store.size, 0, 'expired entry is dropped on lookup')
}

// --- store: oldest-first eviction --------------------------------------------
{
  const c = clock()
  const store = new IdempotencyStore({ maxEntries: 3, now: c.now })
  for (const k of ['a', 'b', 'c', 'd']) { store.remember(k, { id: k }); c.advance(1) }
  assert.equal(store.size, 3)
  assert.equal(store.lookup('a'), null, 'oldest evicted')
  assert.deepEqual(store.lookup('d'), { id: 'd' })
}

// --- store: re-remember refreshes position -----------------------------------
{
  const c = clock()
  const store = new IdempotencyStore({ maxEntries: 2, now: c.now })
  store.remember('a', { id: 'a' }); c.advance(1)
  store.remember('b', { id: 'b' }); c.advance(1)
  store.remember('a', { id: 'a2' }); c.advance(1)   // 'a' becomes newest
  store.remember('c', { id: 'c' })
  assert.equal(store.lookup('b'), null, 'b is now oldest and evicted')
  assert.deepEqual(store.lookup('a'), { id: 'a2' })
}

// --- plan: ordering — connection first ---------------------------------------
{
  const c = clock()
  const error = await expectThrows(planOutboundSend({
    connection: 'disconnected',
    catchUpActive: false,
    idempotency: new IdempotencyStore({ now: c.now }),
    governor: governorAt(c),
    command: { origin: 'ai_auto' }
  }), 'disconnected')
  assert.equal(error.message, 'whatsapp_not_connected')
}

// --- plan: catch-up blocks before any budget is spent ------------------------
{
  const c = clock()
  const governor = governorAt(c)
  const error = await expectThrows(planOutboundSend({
    connection: 'connected',
    catchUpActive: true,
    idempotency: new IdempotencyStore({ now: c.now }),
    governor,
    command: { origin: 'ai_auto' }
  }), 'catch-up active')
  assert.equal(error.message, 'catchup_in_progress')
  assert.equal(governor.snapshot().dailyTotal, 0, 'a blocked send must not consume quota')
}

// --- plan: replay returns the first result and skips the budget ---------------
{
  const c = clock()
  const idempotency = new IdempotencyStore({ now: c.now })
  const governor = governorAt(c, { dailyCap: 1 })
  const base = { connection: 'connected', catchUpActive: false, idempotency, governor }

  const slot = await planOutboundSend({ ...base, command: { origin: 'human', idempotencyKey: 'k' } })
  assert.equal(slot.replayed, undefined)
  commitOutboundSend({ slot, result: { id: 'msg-1' }, idempotency, governor })
  assert.equal(governor.snapshot().dailyTotal, 1)

  // Same key again — the C# side timed out and retried.
  const replay = await planOutboundSend({ ...base, command: { origin: 'human', idempotencyKey: 'k' } })
  assert.deepEqual(replay.replayed, { id: 'msg-1', idempotentReplay: true })
  assert.equal(governor.snapshot().dailyTotal, 1, 'replay must not consume a second slot')

  // And committing a replay is a no-op.
  commitOutboundSend({ slot: replay, result: { id: 'msg-1' }, idempotency, governor })
  assert.equal(governor.snapshot().dailyTotal, 1)

  // A different key on an exhausted budget is correctly refused.
  const error = await expectThrows(
    planOutboundSend({ ...base, command: { origin: 'human', idempotencyKey: 'other' } }),
    'daily cap'
  )
  assert.ok(error instanceof OutboundBlockedError)
  assert.equal(error.code, 'outbound_daily_cap_reached')
}

// --- plan: no key means no replay protection (but still governed) ------------
{
  const c = clock()
  const idempotency = new IdempotencyStore({ now: c.now })
  const governor = governorAt(c)
  const base = { connection: 'connected', catchUpActive: false, idempotency, governor }
  for (let i = 0; i < 3; i += 1) {
    const slot = await planOutboundSend({ ...base, command: { origin: 'campaign' } })
    assert.equal(slot.key, '')
    commitOutboundSend({ slot, result: { id: `m${i}` }, idempotency, governor })
  }
  assert.equal(governor.snapshot().dailyCounts.campaign, 3)
  assert.equal(idempotency.size, 0, 'keyless sends are not stored')
}

// --- plan: missing governor fails closed --------------------------------------
{
  const c = clock()
  const error = await expectThrows(planOutboundSend({
    connection: 'connected',
    catchUpActive: false,
    idempotency: new IdempotencyStore({ now: c.now }),
    governor: null,
    command: {}
  }), 'no governor')
  assert.equal(error.message, 'bridge_not_initialized',
    'an uninitialized bridge must refuse to send, not send ungoverned')
}

// --- plan: origin defaults to human ------------------------------------------
{
  const c = clock()
  const idempotency = new IdempotencyStore({ now: c.now })
  const governor = governorAt(c)
  const slot = await planOutboundSend({
    connection: 'connected', catchUpActive: false, idempotency, governor, command: {}
  })
  assert.equal(slot.origin, 'human')
  commitOutboundSend({ slot, result: { id: 'x' }, idempotency, governor })
  assert.equal(governor.snapshot().dailyCounts.human, 1)
}

// --- plan: an acquired-but-never-committed slot leaves no replay entry --------
{
  const c = clock()
  const idempotency = new IdempotencyStore({ now: c.now })
  const governor = governorAt(c)
  const slot = await planOutboundSend({
    connection: 'connected', catchUpActive: false, idempotency, governor,
    command: { origin: 'ai_auto', idempotencyKey: 'failed-send' }
  })
  // Simulate the send throwing after the reservation: nothing is committed.
  assert.equal(idempotency.lookup('failed-send'), null)
  assert.equal(governor.snapshot().dailyTotal, 0, 'uncommitted send is not counted')
  assert.equal(slot.origin, 'ai_auto')
}

console.log('outbound-idempotency smoke: 11 scenarios passed')
