// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import readline from 'node:readline'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const encryptionKey = Buffer.alloc(32, 7).toString('base64')
const packagedExecutable = process.argv[2]
const smokeLocalAppData = path.join(os.tmpdir(), `waflow-bridge-smoke-${process.pid}`)
const smokeSession = path.join(smokeLocalAppData, 'WAFlow', 'whatsapp-sessions', 'smoke')
await fs.mkdir(smokeSession, { recursive: true })
await fs.writeFile(path.join(smokeSession, 'creds.json.enc'), 'deliberately-unreadable-auth-state')
const child = packagedExecutable
  ? spawn(path.resolve(packagedExecutable), [], { stdio: ['pipe', 'pipe', 'inherit'], env: { ...process.env, LOCALAPPDATA: smokeLocalAppData } })
  : spawn(process.execPath, [path.join(here, '..', 'src', 'index.mjs')], { stdio: ['pipe', 'pipe', 'inherit'], env: { ...process.env, LOCALAPPDATA: smokeLocalAppData } })
const output = readline.createInterface({ input: child.stdout })
let ready = false
let ping = false
let initialized = false
let authRecoveryEvent = false
let authRecoveryValidated = false
let groupCommandValidated = false
let numberValidationCommandValidated = false

const timeout = setTimeout(() => {
  child.kill()
  console.error('FAIL bridge smoke timeout')
  process.exitCode = 1
}, 15000)

output.on('line', line => {
  const message = JSON.parse(line)
  if (message.type === 'event' && message.event === 'ready') {
    ready = true
    child.stdin.write(`${JSON.stringify({ command: 'ping', requestId: 'ping-1' })}\n`)
  } else if (message.type === 'response' && message.requestId === 'ping-1' && message.ok) {
    ping = true
    child.stdin.write(`${JSON.stringify({ command: 'initialize', requestId: 'init-1', accountId: 'smoke', encryptionKey })}\n`)
  } else if (message.type === 'response' && message.requestId === 'init-1' && message.ok) {
    initialized = true
    child.stdin.write(`${JSON.stringify({ command: 'validate_session', requestId: 'validate-1' })}\n`)
  } else if (message.type === 'event' && message.event === 'auth_recovery') {
    authRecoveryEvent = message.data?.requiresQr === true && message.data?.reason === 'local_session_unreadable'
  } else if (message.type === 'response' && message.requestId === 'validate-1' && message.ok) {
    authRecoveryValidated = message.result?.recovered === true
    child.stdin.write(`${JSON.stringify({ command: 'validate_number', requestId: 'number-1', phone: '447700900123' })}\n`)
  } else if (message.type === 'response' && message.requestId === 'number-1') {
    numberValidationCommandValidated = message.ok === false && message.error?.code === 'whatsapp_not_connected'
    child.stdin.write(`${JSON.stringify({ command: 'create_group', requestId: 'group-1', subject: 'Smoke group', participants: ['447700900123'] })}\n`)
  } else if (message.type === 'response' && message.requestId === 'group-1') {
    groupCommandValidated = message.ok === false && message.error?.code === 'whatsapp_not_connected'
    clearTimeout(timeout)
    child.stdin.end()
  }
})

child.on('exit', async () => {
  await fs.rm(smokeLocalAppData, { recursive: true, force: true })
  if (ready && ping && initialized && authRecoveryEvent && authRecoveryValidated && numberValidationCommandValidated && groupCommandValidated) console.log(`PASS WAFlow WhatsApp bridge ready/ping/initialize/auth-recovery/number-validation/group-protocol${packagedExecutable ? ' (packaged EXE)' : ''}`)
  else {
    console.error(`FAIL bridge smoke ready=${ready} ping=${ping} initialized=${initialized} authEvent=${authRecoveryEvent} authValidated=${authRecoveryValidated} numberValidation=${numberValidationCommandValidated} group=${groupCommandValidated}`)
    process.exitCode = 1
  }
})
