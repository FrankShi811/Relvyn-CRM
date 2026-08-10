// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

const fs = require('node:fs')
const path = require('node:path')
const { pathToFileURL } = require('node:url')
const { getAsset } = require('node:sea')

function fail(error) {
  const message = error instanceof Error ? error.stack || error.message : String(error)
  process.stderr.write(`${message}\n`)
  process.exitCode = 1
}

async function main() {
  const manifest = JSON.parse(getAsset('manifest.json', 'utf8'))
  const configuredRoot = String(process.env.WAFLOW_DATA_ROOT || '').trim()
  const localAppData = process.env.LOCALAPPDATA
  if (!configuredRoot && !localAppData) throw new Error('LOCALAPPDATA_not_available')
  const dataRoot = configuredRoot ? path.resolve(configuredRoot) : path.join(localAppData, 'WAFlow')
  const runtimeRoot = path.join(dataRoot, 'bridge-runtime', manifest.hash)
  fs.mkdirSync(runtimeRoot, { recursive: true, mode: 0o700 })

  for (const file of manifest.files) {
    const destination = path.resolve(runtimeRoot, file.path)
    if (!destination.startsWith(path.resolve(runtimeRoot) + path.sep)) throw new Error('invalid_embedded_asset_path')
    fs.mkdirSync(path.dirname(destination), { recursive: true, mode: 0o700 })
    const current = fs.existsSync(destination) ? fs.statSync(destination) : null
    if (current?.size === file.size) continue
    const temporary = `${destination}.${process.pid}.tmp`
    fs.writeFileSync(temporary, new Uint8Array(getAsset(`bridge/${file.path}`)), { mode: 0o600 })
    fs.rmSync(destination, { force: true })
    fs.renameSync(temporary, destination)
  }

  await import(pathToFileURL(path.join(runtimeRoot, manifest.entry)).href)
}

main().catch(fail)
