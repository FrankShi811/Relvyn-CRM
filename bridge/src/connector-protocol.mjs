// SPDX-License-Identifier: GPL-3.0-only

export const BRIDGE_NAME = 'WAFlow.WhatsApp.Bridge'
export const BRIDGE_VERSION = '0.9.0'
export const PROTOCOL_VERSION = 1
export const CONNECTOR_NAME = 'baileys'
export const CONNECTOR_VERSION = '7.0.0-rc13'

// Additive capability names are intentionally connector-neutral. Every value
// below describes a behavior already shipped by the stable Baileys connector.
export const STABLE_CAPABILITIES = Object.freeze({
  multiAccount: true,
  qrPairing: true,
  sessionPersistence: true,
  directMessages: true,
  groupMessages: true,
  historySync: true,
  offlineCatchup: true,
  mediaReceive: true,
  textSend: true,
  mediaSend: true,
  reply: true,
  revoke: true,
  deliveryReceipts: true,
  readReceipts: true,
  numberValidation: true,
  pinChat: true,
  groups: true,
  labels: true,
  lidMapping: true,
  outboundGovernor: true,
  idempotency: true
})

export function connectorMetadata(connection = 'idle') {
  return {
    bridge: BRIDGE_NAME,
    // `version` is retained for desktop clients released before protocol v1.
    version: BRIDGE_VERSION,
    bridgeVersion: BRIDGE_VERSION,
    protocolVersion: PROTOCOL_VERSION,
    connector: CONNECTOR_NAME,
    connectorVersion: CONNECTOR_VERSION,
    capabilities: { ...STABLE_CAPABILITIES },
    connection
  }
}
