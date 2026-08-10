// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

const DEFAULT_TIMEOUT_MS = 8000
export const BUNDLED_VALIDATED_VERSION = [2, 3000, 1043857760]

function validVersion(value) {
  return Array.isArray(value)
    && value.length === 3
    && value.every(part => Number.isInteger(part) && part >= 0)
}

function normalizeFetchers(fetchVersions) {
  if (typeof fetchVersions === 'function') {
    return [{ source: 'remote', fetch: fetchVersions }]
  }
  return (Array.isArray(fetchVersions) ? fetchVersions : [])
    .filter(item => item && typeof item.fetch === 'function')
    .map((item, index) => ({
      source: String(item.source ?? `remote_${index + 1}`).trim() || `remote_${index + 1}`,
      fetch: item.fetch
    }))
}

async function fetchWithTimeout(fetchVersion, timeoutMs) {
  let timer
  try {
    return await Promise.race([
      Promise.resolve().then(fetchVersion),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('version_lookup_timeout')), timeoutMs)
      })
    ])
  } finally {
    if (timer) clearTimeout(timer)
  }
}

export async function resolveBaileysVersion(fetchVersions, options = {}) {
  const cachedVersion = validVersion(options.cachedVersion) ? options.cachedVersion : null
  const disabled = options.disabled === true
    || process.env.WAFLOW_BAILEYS_VERSION_LOOKUP_DISABLED === '1'
  if (disabled) {
    return {
      version: cachedVersion ?? BUNDLED_VALIDATED_VERSION,
      source: cachedVersion ? 'cached' : 'bundled',
      warning: 'online_version_lookup_disabled'
    }
  }

  const timeoutMs = Number.isFinite(options.timeoutMs)
    ? Math.max(50, Number(options.timeoutMs))
    : DEFAULT_TIMEOUT_MS
  const warnings = []
  for (const candidate of normalizeFetchers(fetchVersions)) {
    try {
      const result = await fetchWithTimeout(candidate.fetch, timeoutMs)
      if (result?.isLatest === false) {
        throw new Error(result?.error?.message ?? 'version_source_not_latest')
      }
      if (!validVersion(result?.version)) throw new Error('invalid_version_response')
      return { version: result.version, source: candidate.source, warning: '' }
    } catch (error) {
      warnings.push(`${candidate.source}:${String(error?.message ?? error ?? 'version_lookup_failed')}`)
    }
  }

  return {
    version: cachedVersion ?? BUNDLED_VALIDATED_VERSION,
    source: cachedVersion ? 'cached' : 'bundled',
    warning: warnings.join(';') || 'version_lookup_failed'
  }
}
