// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

import { spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..')
const dist = path.join(root, 'dist')
const nccOutput = path.join(dist, 'ncc')
const blob = path.join(dist, 'waflow-bridge.blob')
const executable = path.join(dist, 'WAFlow.WhatsApp.Bridge.exe')
const node = process.execPath
const skipNcc = process.argv.includes('--skip-ncc')

async function run(file, args, options = {}) {
  await new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd: root, stdio: 'inherit', ...options })
    child.once('error', reject)
    child.once('exit', code => code === 0 ? resolve() : reject(new Error(`${path.basename(file)} exited with code ${code}`)))
  })
}

async function listFiles(directory, prefix = '') {
  const output = []
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name
    if (entry.isDirectory()) output.push(...await listFiles(path.join(directory, entry.name), relative))
    else output.push(relative.replaceAll('\\', '/'))
  }
  return output.sort()
}

if (!skipNcc) {
  await fs.rm(dist, { recursive: true, force: true })
  await fs.mkdir(nccOutput, { recursive: true })
  await run(node, [path.join(root, 'node_modules', '@vercel', 'ncc', 'dist', 'ncc', 'cli.js'), 'build', path.join(root, 'src', 'index.mjs'), '-o', nccOutput, '--no-cache', '--license', 'THIRD_PARTY_LICENSES.txt'])
} else if (!(await fs.stat(path.join(nccOutput, 'index.mjs')).catch(() => null))) {
  throw new Error('Cannot skip ncc because dist/ncc/index.mjs does not exist')
}

const files = await listFiles(nccOutput)
if (!files.includes('index.mjs')) throw new Error('ncc output entry index.mjs is missing')
const hash = crypto.createHash('sha256')
const manifestFiles = []
for (const file of files) {
  const content = await fs.readFile(path.join(nccOutput, file))
  hash.update(file); hash.update(content)
  manifestFiles.push({ path: file, size: content.length })
}
const manifest = { version: 1, hash: hash.digest('hex').slice(0, 20), entry: 'index.mjs', files: manifestFiles }
const manifestPath = path.join(dist, 'sea-manifest.json')
await fs.writeFile(manifestPath, JSON.stringify(manifest))

const assets = { 'manifest.json': manifestPath }
for (const file of files) assets[`bridge/${file}`] = path.join(nccOutput, file)
const seaConfig = {
  main: path.join(root, 'scripts', 'sea-bootstrap.cjs'),
  output: blob,
  disableExperimentalSEAWarning: true,
  useSnapshot: false,
  useCodeCache: false,
  assets
}
const seaConfigPath = path.join(dist, 'sea-config.json')
await fs.writeFile(seaConfigPath, JSON.stringify(seaConfig, null, 2))
await run(node, ['--experimental-sea-config', seaConfigPath])
await fs.copyFile(node, executable)
await run(node, [
  path.join(root, 'node_modules', 'postject', 'dist', 'cli.js'), executable, 'NODE_SEA_BLOB', blob,
  '--sentinel-fuse', 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2'
])
console.log(`PASS built ${executable} (${manifestFiles.length} embedded files, runtime ${manifest.hash})`)
