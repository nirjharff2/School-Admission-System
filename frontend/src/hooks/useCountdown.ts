import { useEffect, useState } from 'react'

/**
 * Browser time, refreshed every second while `active` (and exactly when `deadline` passes, so a countdown does not show
 * 0:01 for a second after it ended). Use it to render a countdown or to flip a state when a time has passed.
 */
export function useNow(active: boolean, deadline?: number): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (!active) return
    const tick = () => setNow(Date.now())
    const interval = setInterval(tick, 1000)
    const untilDeadline = deadline === undefined ? Infinity : deadline - Date.now()
    const timeout = untilDeadline > 0 && untilDeadline < 2 ** 31 - 1 ? setTimeout(tick, untilDeadline) : undefined
    return () => {
      clearInterval(interval)
      clearTimeout(timeout)
    }
  }, [active, deadline])

  return now
}

/** Milliseconds left until `expiresAt` (ISO-8601), ticking every second; never negative; null if the text is not a date. */
export function useCountdown(expiresAt: string, active = true): { now: number; remainingMs: number | null } {
  const deadline = Date.parse(expiresAt)
  const now = useNow(active, Number.isNaN(deadline) ? undefined : deadline)
  return { now, remainingMs: Number.isNaN(deadline) ? null : Math.max(0, deadline - now) }
}

/** 9:05, or 1:02:03 when an hour or more is left; rounds up so the display reaches 0:00 exactly when time is up. */
export function formatRemaining(ms: number): string {
  const total = Math.ceil(ms / 1000)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = total % 60
  const ss = String(seconds).padStart(2, '0')
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${ss}` : `${minutes}:${ss}`
}
