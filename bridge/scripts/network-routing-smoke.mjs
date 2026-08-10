// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import { createProxyAgent, normalizeProxyUrl, safeProxyLabel } from '../src/network-routing.mjs'

assert.equal(normalizeProxyUrl(''), '')
assert.equal(normalizeProxyUrl('127.0.0.1:7890'), 'http://127.0.0.1:7890/')
assert.match(createProxyAgent('http://127.0.0.1:7890')?.constructor?.name ?? '', /ProxyAgent/i)
assert.match(createProxyAgent('socks5://127.0.0.1:1080')?.constructor?.name ?? '', /SocksProxyAgent/i)
assert.equal(safeProxyLabel('http://user:secret@127.0.0.1:7890', 'windows'), 'Windows 系统代理 (http://127.0.0.1:7890)')
assert.throws(() => normalizeProxyUrl('file:///tmp/proxy'), /unsupported_proxy_url/)

console.log('PASS network proxy routing and credential redaction')
