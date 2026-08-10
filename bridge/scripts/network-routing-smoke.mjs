// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import http from 'node:http'
import net from 'node:net'
import { createFetchDispatcher, createProxyAgent, normalizeProxyUrl, safeProxyLabel } from '../src/network-routing.mjs'

assert.equal(normalizeProxyUrl(''), '')
assert.equal(normalizeProxyUrl('127.0.0.1:7890'), 'http://127.0.0.1:7890/')
assert.match(createProxyAgent('http://127.0.0.1:7890')?.constructor?.name ?? '', /ProxyAgent/i)
assert.match(createProxyAgent('socks5://127.0.0.1:1080')?.constructor?.name ?? '', /SocksProxyAgent/i)
const httpDispatcher = createFetchDispatcher('http://127.0.0.1:7890')
const socksDispatcher = createFetchDispatcher('socks5://127.0.0.1:1080')
assert.match(httpDispatcher?.constructor?.name ?? '', /ProxyAgent/i)
assert.match(socksDispatcher?.constructor?.name ?? '', /Agent/i)
assert.equal(safeProxyLabel('http://user:secret@127.0.0.1:7890', 'windows'), 'Windows 系统代理 (http://127.0.0.1:7890)')
assert.throws(() => normalizeProxyUrl('file:///tmp/proxy'), /unsupported_proxy_url/)

await httpDispatcher.close()
await socksDispatcher.close()

const target = http.createServer((request, response) => {
  response.writeHead(200, { 'content-type': 'application/octet-stream' })
  response.end(Buffer.from('app-state-snapshot'))
})
await listen(target)
const targetAddress = target.address()

let proxyRequests = 0
const proxy = http.createServer((request, response) => {
  proxyRequests += 1
  const destination = new URL(request.url)
  const upstream = http.request(destination, { method: request.method, headers: request.headers }, upstreamResponse => {
    response.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.headers)
    upstreamResponse.pipe(response)
  })
  upstream.on('error', error => response.destroy(error))
  request.pipe(upstream)
})
proxy.on('connect', (request, clientSocket, head) => {
  proxyRequests += 1
  const [host, portText] = String(request.url ?? '').split(':')
  const upstreamSocket = net.connect(Number(portText), host, () => {
    clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n')
    if (head.length) upstreamSocket.write(head)
    upstreamSocket.pipe(clientSocket)
    clientSocket.pipe(upstreamSocket)
  })
  upstreamSocket.on('error', error => clientSocket.destroy(error))
})
await listen(proxy)
const proxyAddress = proxy.address()
const liveDispatcher = createFetchDispatcher(`http://127.0.0.1:${proxyAddress.port}`)
try {
  const response = await fetch(`http://127.0.0.1:${targetAddress.port}/snapshot`, { dispatcher: liveDispatcher })
  assert.equal(response.status, 200)
  assert.equal(await response.text(), 'app-state-snapshot')
  assert.equal(proxyRequests, 1)
} finally {
  await liveDispatcher.close()
  await close(proxy)
  await close(target)
}

console.log('PASS network proxy routing, native fetch dispatcher and credential redaction')

function listen(server) {
  return new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
}

function close(server) {
  return new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
}
