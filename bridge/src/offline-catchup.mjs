// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 AI Sales OS contributors

export const DEFAULT_OFFLINE_CATCHUP_TIMEOUT_MS = 30000
export const DEFAULT_OFFLINE_CATCHUP_SETTLE_MS = 6000

export class OfflineCatchupCoordinator {
  constructor({ timeoutMs = DEFAULT_OFFLINE_CATCHUP_TIMEOUT_MS, settleMs = DEFAULT_OFFLINE_CATCHUP_SETTLE_MS, enqueue, emitStatus, emitIssue, emitSnapshot, getTotals }) {
    this.timeoutMs = timeoutMs
    this.settleMs = settleMs
    this.enqueue = enqueue
    this.emitStatus = emitStatus
    this.emitIssue = emitIssue
    this.emitSnapshot = emitSnapshot
    this.getTotals = getTotals
    this.active = null
  }

  cancel() {
    if (this.active?.timer) clearTimeout(this.active.timer)
    if (this.active?.settleTimer) clearTimeout(this.active.settleTimer)
    this.active = null
  }

  async start({ socket, attempt, source, existingSession }) {
    this.cancel()
    if (!existingSession || !socket) return false

    const active = {
      socket, attempt, source, received: false, timer: null, settleTimer: null,
      baselineMessages: Number(this.getTotals()?.messages ?? 0),
      recoveredMessages: 0,
      requestedChats: 0
    }
    this.active = active
    this.emitStatus({ state: 'syncing', phase: 'offline_messages', progress: null, source })

    try {
      // A temporary "available" presence makes Baileys send its unified-session
      // request. WhatsApp then flushes messages queued while this desktop was
      // offline. We switch back to "unavailable" after the queue is drained so
      // the phone keeps receiving its own notifications.
      await socket.sendPresenceUpdate('available')
    } catch (error) {
      if (this.active !== active) return false
      this.emitIssue({
        code: 'offline_catchup_presence_failed',
        recoverable: true,
        message: '离线消息补齐请求暂未送达，程序将继续保持实时监听',
        error
      })
    }

    if (this.active !== active) return false
    if (active.received) {
      this.scheduleSettle(active)
      return true
    }

    active.timer = setTimeout(() => {
      this.enqueue(() => this.finish(active, true))
    }, this.timeoutMs)
    return true
  }

  receivePending({ socket, attempt }) {
    const active = this.active
    if (!active || active.socket !== socket || active.attempt !== attempt) return false
    active.received = true
    if (active.timer) {
      clearTimeout(active.timer)
      active.timer = null
    }
    this.scheduleSettle(active)
    return true
  }

  noteHistoryRequest(count = 1) {
    const active = this.active
    if (!active) return false
    active.requestedChats += Math.max(0, Number(count) || 0)
    if (active.received) this.scheduleSettle(active)
    return true
  }

  noteRecoveredMessages(count = 1) {
    const active = this.active
    if (!active) return false
    active.recoveredMessages += Math.max(0, Number(count) || 0)
    if (active.received) this.scheduleSettle(active)
    return true
  }

  scheduleSettle(active) {
    if (this.active !== active) return
    if (active.settleTimer) clearTimeout(active.settleTimer)
    active.settleTimer = setTimeout(() => this.enqueue(() => this.finish(active, false)), this.settleMs)
  }

  async finish(active, timedOut) {
    if (this.active !== active) return false
    this.active = null
    if (active.timer) clearTimeout(active.timer)
    if (active.settleTimer) clearTimeout(active.settleTimer)

    try { await active.socket.sendPresenceUpdate('unavailable') } catch { }
    const counts = await this.emitSnapshot(`catchup:${active.source}`)
    const totals = this.getTotals()
    const totalDelta = Math.max(0, Number(totals?.messages ?? 0) - active.baselineMessages)
    const recoveredMessages = Math.max(active.recoveredMessages, totalDelta)
    this.emitStatus({
      state: 'complete',
      phase: recoveredMessages > 0
        ? 'offline_messages'
        : timedOut ? 'offline_messages_timeout' : 'offline_messages_no_new_messages',
      progress: 100,
      source: active.source,
      pendingNotificationsReceived: !timedOut,
      recoveredMessages,
      requestedChats: active.requestedChats,
      ...counts,
      ...totals
    })
    return true
  }
}
