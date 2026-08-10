// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import readline from 'node:readline'

const dataRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'waflow-bridge-lifecycle-'))
const child = spawn(process.execPath, ['src/index.mjs'], {
  cwd: path.resolve(import.meta.dirname, '..'),
  env: {
    ...process.env,
    WAFLOW_DATA_ROOT: dataRoot
  },
  stdio: ['pipe', 'pipe', 'pipe']
})

const messages = []
const errors = []
const waiters = []
let currentStage = 'bridge startup'
const output = readline.createInterface({ input: child.stdout, crlfDelay: Infinity })
const errorOutput = readline.createInterface({ input: child.stderr, crlfDelay: Infinity })
errorOutput.on('line', line => errors.push(line))
output.on('line', line => {
  let message
  try { message = JSON.parse(line) } catch { return }
  messages.push(message)
  for (const waiter of [...waiters]) {
    if (!waiter.predicate(message)) continue
    clearTimeout(waiter.timer)
    waiters.splice(waiters.indexOf(waiter), 1)
    waiter.resolve(message)
  }
})

function waitFor(predicate, timeoutMs = 15000) {
  const existing = messages.find(predicate)
  if (existing) return Promise.resolve(existing)
  return new Promise((resolve, reject) => {
    const waiter = {
      predicate,
      resolve,
      timer: setTimeout(() => {
        waiters.splice(waiters.indexOf(waiter), 1)
        reject(new Error(`bridge_event_timeout_${timeoutMs} during ${currentStage}; messages=${JSON.stringify(messages.slice(-8))}; stderr=${errors.slice(-8).join(' | ')}`))
      }, timeoutMs)
    }
    waiters.push(waiter)
  })
}

let requestSequence = 0
async function command(name, payload = {}, timeoutMs = 15000) {
  const requestId = `smoke-${++requestSequence}`
  child.stdin.write(`${JSON.stringify({ command: name, requestId, ...payload })}\n`)
  const response = await waitFor(message => message.type === 'response' && message.requestId === requestId, timeoutMs)
  assert.equal(response.ok, true, `${name} failed: ${JSON.stringify(response.error ?? {})}`)
  return response.result ?? {}
}

try {
  currentStage = 'bridge ready'
  // Source-mode cold starts can spend tens of seconds in dependency loading on
  // Windows antivirus-scanned worktrees. Runtime commands retain the stricter
  // 15-second timeout; only the source-test bootstrap gets a wider allowance.
  await waitFor(message => message.type === 'event' && message.event === 'ready', 60000)
  currentStage = 'initialize'
  await command('initialize', {
    accountId: 'lifecycle_smoke',
    encryptionKey: crypto.randomBytes(32).toString('base64')
  })

  currentStage = 'invalid proxy fallback'
  await command('connect', {
    proxyUrl: 'http://127.0.0.1:1',
    proxySource: 'lifecycle-smoke',
    allowDirectFallback: true
  })
  await waitFor(message => message.type === 'event'
    && message.event === 'connection_issue'
    && message.data?.code === 'proxy_route_failed', 30000)

  currentStage = 'manual disconnect'
  const disconnectedAt = messages.length
  const disconnected = await command('disconnect')
  assert.equal(disconnected.state, 'disconnected')
  await waitFor(message => message.type === 'event'
    && message.event === 'connection'
    && message.data?.state === 'disconnected'
    && message.data?.manual === true)
  await new Promise(resolve => setTimeout(resolve, 6500))
  assert.equal(messages.slice(disconnectedAt).some(message =>
    message.type === 'event'
      && message.event === 'connection'
      && ['connecting', 'retrying'].includes(message.data?.state)), false,
  'automatic reconnect resumed after manual disconnect')

  currentStage = 'forced logout reset'
  const sessionDirectory = path.join(dataRoot, 'whatsapp-sessions', 'lifecycle_smoke')
  const markerPath = path.join(sessionDirectory, 'stale-session-marker')
  await fs.writeFile(markerPath, 'stale')
  const loggedOut = await command('logout')
  assert.equal(loggedOut.state, 'logged_out')
  await assert.rejects(fs.access(markerPath))
  await fs.access(sessionDirectory)

  // A completed logout must leave the same bridge process capable of starting
  // a brand-new pairing and producing a real, scannable QR milestone. This is
  // the exact lifecycle exercised by Logout -> Connect / Show QR in the desktop.
  currentStage = 'fresh QR pairing'
  const qrSearchStartedAt = messages.length
  await command('connect', {
    proxyUrl: '',
    proxySource: 'lifecycle-smoke-fresh-pairing',
    allowDirectFallback: true
  })
  const qrEvent = await waitFor(message => message.type === 'event'
    && message.event === 'qr'
    && typeof message.data?.dataUrl === 'string'
    && message.data.dataUrl.startsWith('data:image/png;base64,'), 90000)
  assert.ok(qrEvent.data.dataUrl.length > 100, 'fresh pairing QR data URL was empty')
  assert.equal(messages.slice(qrSearchStartedAt).some(message =>
    message.type === 'event'
      && message.event === 'connection_stage'
      && message.data?.clientProfile === 'windows_chrome_pairing'), true,
  'fresh pairing did not use the WhatsApp Web compatible Windows Chrome profile')
  assert.equal(messages.slice(qrSearchStartedAt).some(message =>
    message.type === 'event'
      && message.event === 'connection'
      && message.data?.state === 'connected'), false,
  'cleared test session unexpectedly resumed as an old linked device')
  await command('disconnect')

  console.log('PASS WhatsApp proxy fallback, manual disconnect cancellation, forced logout reset, and fresh QR pairing')
} finally {
  output.close()
  errorOutput.close()
  child.stdin.end()
  if (!child.killed) child.kill()
  await fs.rm(dataRoot, { recursive: true, force: true })
}
