// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { HttpsProxyAgent } from 'https-proxy-agent'
import { SocksProxyAgent } from 'socks-proxy-agent'

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
