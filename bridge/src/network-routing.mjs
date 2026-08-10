// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { HttpsProxyAgent } from 'https-proxy-agent'
import { SocksProxyAgent } from 'socks-proxy-agent'
import { Agent as UndiciAgent, ProxyAgent as UndiciProxyAgent } from 'undici'

const allowedSchemes = new Set(['http:', 'https:', 'socks:', 'socks5:', 'socks5h:'])

export function normalizeProxyUrl(value) {
  const candidate = String(value ?? '').trim()
  if (!candidate) return ''
  const parsed = new URL(candidate.includes('://') ? candidate : `http://${candidate}`)
  if (!allowedSchemes.has(parsed.protocol) || !parsed.hostname) throw new Error('unsupported_proxy_url')
  return parsed.href
}

export function createProxyAgent(value) {
  const proxyUrl = normalizeProxyUrl(value)
  if (!proxyUrl) return null
  const parsed = new URL(proxyUrl)
  return parsed.protocol.startsWith('socks')
    ? new SocksProxyAgent(proxyUrl)
    : new HttpsProxyAgent(proxyUrl)
}

// Baileys uses two different HTTP stacks. WebSocket/media uploads use the
// classic Node Agent above, while app-state snapshots are downloaded with
// native fetch and therefore require an Undici Dispatcher. Keeping both routes
// on the same proxy is essential: a connected socket does not prove that the
// encrypted app-state snapshot (labels, pins, archive state) is reachable.
export function createFetchDispatcher(value) {
  const proxyUrl = normalizeProxyUrl(value)
  if (!proxyUrl) return null
  const parsed = new URL(proxyUrl)
  if (!parsed.protocol.startsWith('socks')) return new UndiciProxyAgent(proxyUrl)

  const socksAgent = new SocksProxyAgent(proxyUrl)
  return new UndiciAgent({
    connect(options, callback) {
      const host = String(options.hostname ?? options.host ?? '')
      const port = Number(options.port || (options.protocol === 'https:' ? 443 : 80))
      const request = { destroy() {} }
      socksAgent.connect(request, {
        ...options,
        host,
        port,
        secureEndpoint: options.protocol === 'https:'
      }).then(socket => callback(null, socket), callback)
    }
  })
}

export function safeProxyLabel(value, source = '') {
  const proxyUrl = normalizeProxyUrl(value)
  if (!proxyUrl) return '直连'
  const parsed = new URL(proxyUrl)
  const prefix = source === 'windows' ? 'Windows 系统代理' : '环境代理'
  return `${prefix} (${parsed.protocol}//${parsed.hostname}:${parsed.port || defaultPort(parsed.protocol)})`
}

function defaultPort(protocol) {
  if (protocol === 'http:') return '80'
  if (protocol === 'https:') return '443'
  return '1080'
}
