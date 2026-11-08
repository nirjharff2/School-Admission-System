import type { HoldStatus } from './api/client'

export interface EffectiveStatus {
  status: HoldStatus
  /** True when the server still says HELD although the browser clock says the hold has run out. */
  serverDisagrees: boolean
}

/**
 * The status to show right now. The server is the authority, but its answer is only as fresh as the last fetch, so a
 * `HELD` hold whose `expiresAt` has passed is shown as `EXPIRED` at once, without waiting for the next poll.
 * If the browser clock is ahead of the server's, that guess can be wrong; a fetch sent after `expiresAt` that still
 * says `HELD` therefore overrules the local clock (`serverDisagrees`), until the server catches up and says `EXPIRED`.
 */
export function effectiveStatus(status: HoldStatus, expiresAt: string, now: number, requestedAt: number): EffectiveStatus {
  const deadline = Date.parse(expiresAt)
  const lapsed = status === 'HELD' && !Number.isNaN(deadline) && now >= deadline
  if (!lapsed) return { status, serverDisagrees: false }
  return requestedAt >= deadline ? { status, serverDisagrees: true } : { status: 'EXPIRED', serverDisagrees: false }
}

/** Wait after `expiresAt` before refetching, so the server's own clock has certainly passed it too. */
const EXPIRY_GRACE_MS = 500
/** Pace while a hold's expiry has passed locally but the server has not confirmed it yet. */
const EXPIRY_SYNC_MS = 2000

/**
 * Milliseconds until the next automatic refetch for a screen showing `holds`: every `slowMs` while any is HELD, but
 * sooner when one is about to expire (just after `expiresAt`) and every couple of seconds while the server has not yet
 * confirmed an expiry the clock says happened. With no HELD hold the pace is `idleMs` (null = stop: nothing can change).
 */
export function refreshDelayFor(
  holds: readonly { status: HoldStatus; expiresAt: string }[],
  now: number,
  slowMs: number,
  idleMs: number | null,
): number | null {
  let delay = idleMs
  for (const hold of holds) {
    if (hold.status !== 'HELD') continue
    const deadline = Date.parse(hold.expiresAt)
    const untilExpiry = Number.isNaN(deadline) ? slowMs : deadline + EXPIRY_GRACE_MS - now
    delay = Math.min(delay ?? slowMs, slowMs, untilExpiry > 0 ? untilExpiry : EXPIRY_SYNC_MS)
  }
  return delay
}
