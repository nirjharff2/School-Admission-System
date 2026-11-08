import { useEffect, useEffectEvent, useRef, useState } from 'react'
import { toFailure } from '../api/client'

type Settled<T> = { key: string } & (
  | { value: T; requestedAt: number; refreshError: string | null }
  | { error: string }
)

export type Loaded<T> =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  /**
   * `requestedAt` is the browser time when the request that produced `value` was sent (a value set by `mutate` has the
   * time of the mutation). `refreshError` is set while the latest background refetch failed and `value` is older.
   */
  | { state: 'ready'; value: T; requestedAt: number; refreshError: string | null }

export interface LoadOptions<T> {
  /**
   * Milliseconds until the next automatic refetch, given the data on screen (undefined before the first answer) and
   * the current time; null stops polling. Evaluated each time an answer arrives, so the pace can change with the data.
   * Refetching also happens whenever the tab becomes visible or the window regains focus.
   */
  refreshDelay?: (value: T | undefined, now: number) => number | null
}

const MIN_DELAY_MS = 250

/**
 * Loads data for `key` (null = nothing to load).
 * - A new key shows the loading state.
 * - `reload` refetches now. Data already on screen stays visible meanwhile and after a failed refetch (`refreshError`
 *   says so); an error from the first load turns into the loading state again.
 * - `mutate` shows a value the caller already knows right away and refetches to confirm it.
 * - Automatic refetching (`refreshDelay`, focus) never runs two requests at once and never runs in a hidden tab.
 * Answers to requests that were superseded (new key, new attempt, screen closed) are dropped.
 */
export function useLoad<T>(key: string | null, load: () => Promise<T>, options: LoadOptions<T> = {}) {
  const [attempt, setAttempt] = useState(0)
  const [settled, setSettled] = useState<Settled<T> | null>(null)
  const inFlight = useRef(false)
  const run = useEffectEvent(load)
  const delayFor = useEffectEvent((value: T | undefined, now: number) => options.refreshDelay?.(value, now) ?? null)

  useEffect(() => {
    if (key === null) return
    let current = true
    const requestedAt = Date.now()
    inFlight.current = true
    run().then(
      (value) => {
        if (!current) return
        inFlight.current = false
        setSettled({ key, value, requestedAt, refreshError: null })
      },
      (error: unknown) => {
        if (!current) return
        inFlight.current = false
        const message = toFailure(error).message
        setSettled((previous) =>
          previous !== null && previous.key === key && 'value' in previous
            ? { ...previous, refreshError: message }
            : { key, error: message },
        )
      },
    )
    return () => {
      current = false
      inFlight.current = false
    }
  }, [key, attempt])

  // the next automatic refetch is scheduled after every answer (or failure), so the pace follows the data
  useEffect(() => {
    if (key === null) return
    const value = settled !== null && settled.key === key && 'value' in settled ? settled.value : undefined
    const delay = delayFor(value, Date.now())
    if (delay === null) return
    const id = setTimeout(
      () => {
        if (!inFlight.current && document.visibilityState === 'visible') setAttempt((n) => n + 1)
      },
      Math.max(delay, MIN_DELAY_MS),
    )
    return () => clearTimeout(id)
  }, [key, settled, attempt])

  // coming back to the tab: refetch at once instead of showing what was true minutes ago
  const polls = options.refreshDelay !== undefined
  useEffect(() => {
    if (key === null || !polls) return
    const onReturn = () => {
      if (document.visibilityState === 'visible' && !inFlight.current) setAttempt((n) => n + 1)
    }
    window.addEventListener('focus', onReturn)
    document.addEventListener('visibilitychange', onReturn)
    return () => {
      window.removeEventListener('focus', onReturn)
      document.removeEventListener('visibilitychange', onReturn)
    }
  }, [key, polls])

  let loaded: Loaded<T> = { state: 'loading' }
  if (key !== null && settled !== null && settled.key === key) {
    loaded =
      'value' in settled
        ? { state: 'ready', value: settled.value, requestedAt: settled.requestedAt, refreshError: settled.refreshError }
        : { state: 'error', message: settled.error }
  }

  return {
    loaded,
    reload: () => {
      // an error on screen turns into "loading" while the retry runs
      setSettled((previous) => (previous !== null && 'error' in previous ? null : previous))
      setAttempt((n) => n + 1)
    },
    mutate: (value: T) => {
      if (key === null) return
      setSettled({ key, value, requestedAt: Date.now(), refreshError: null })
      setAttempt((n) => n + 1) // drops any older answer still in flight and refetches the truth
    },
  }
}
