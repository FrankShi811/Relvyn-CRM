// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { BUNDLED_VALIDATED_VERSION, resolveBaileysVersion } from '../src/connection-bootstrap.mjs'

const remote = await resolveBaileysVersion(
  async () => ({ version: [2, 3000, 123456789] }),
  { timeoutMs: 100 }
)
assert.deepEqual(remote.version, [2, 3000, 123456789])
assert.equal(remote.source, 'remote')

const primary = await resolveBaileysVersion([
  { source: 'whatsapp_web', fetch: async () => ({ version: [2, 3000, 987654321], isLatest: true }) },
  { source: 'baileys', fetch: async () => ({ version: [2, 3000, 123456789], isLatest: true }) }
], { timeoutMs: 100 })
assert.deepEqual(primary.version, [2, 3000, 987654321])
assert.equal(primary.source, 'whatsapp_web')

const secondary = await resolveBaileysVersion([
  { source: 'whatsapp_web', fetch: async () => ({ version: [2, 3000, 1], isLatest: false, error: new Error('wa_blocked') }) },
  { source: 'baileys', fetch: async () => ({ version: [2, 3000, 123456789], isLatest: true }) }
], { timeoutMs: 100 })
assert.deepEqual(secondary.version, [2, 3000, 123456789])
assert.equal(secondary.source, 'baileys')

const startedAt = Date.now()
const timeout = await resolveBaileysVersion(
  () => new Promise(() => {}),
  { timeoutMs: 60 }
)
assert.deepEqual(timeout.version, BUNDLED_VALIDATED_VERSION)
assert.equal(timeout.source, 'bundled')
assert.match(timeout.warning, /version_lookup_timeout/)
assert.ok(Date.now() - startedAt < 500)

const rejected = await resolveBaileysVersion(
  async () => { throw new Error('network_blocked') },
  { timeoutMs: 100 }
)
assert.equal(rejected.source, 'bundled')
assert.deepEqual(rejected.version, BUNDLED_VALIDATED_VERSION)
assert.match(rejected.warning, /network_blocked/)

const cached = await resolveBaileysVersion(
  async () => { throw new Error('network_blocked') },
  { timeoutMs: 100, cachedVersion: [2, 3000, 222222222] }
)
assert.equal(cached.source, 'cached')
assert.deepEqual(cached.version, [2, 3000, 222222222])

const invalid = await resolveBaileysVersion(
  async () => ({ version: [] }),
  { timeoutMs: 100 }
)
assert.equal(invalid.source, 'bundled')
assert.deepEqual(invalid.version, BUNDLED_VALIDATED_VERSION)
assert.match(invalid.warning, /invalid_version_response/)

const disabled = await resolveBaileysVersion(
  async () => { throw new Error('must_not_run') },
  { timeoutMs: 100, disabled: true }
)
assert.equal(disabled.source, 'bundled')
assert.deepEqual(disabled.version, BUNDLED_VALIDATED_VERSION)
assert.equal(disabled.warning, 'online_version_lookup_disabled')

console.log('PASS WhatsApp connection bootstrap timeout and bundled-version fallback')
