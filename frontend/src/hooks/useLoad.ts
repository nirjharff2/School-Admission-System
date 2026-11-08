import { useEffect, useEffectEvent, useState } from 'react'
import { toFailure } from '../api/client'

type Settled<T> = { key: string; attempt: number } & ({ value: T } | { error: string })

export type Loaded<T> =
  | { state: 'loading' }
  | { state: 'error'; message: string }
  | { state: 'ready'; value: T }

/**
 * Loads data for `key` (null = nothing to load).
 * - A new key shows the loading state.
 * - `reload` refetches; data already on screen stays visible meanwhile and after a failed refetch, while an error from
 *   the first load turns into the loading state again.
 * - `mutate` shows a value the caller already knows right away and refetches to confirm it.
 * Answers to requests that were superseded (new key, new attempt, screen closed) are dropped.
 */
export function useLoad<T>(key: string | null, load: () => Promise<T>) {
  const [attempt, setAttempt] = useState(0)
  const [settled, setSettled] = useState<Settled<T> | null>(null)
  const run = useEffectEvent(load)

  useEffect(() => {
    if (key === null) return
    let current = true
    run().then(
      (value) => {
        if (current) setSettled({ key, attempt, value })
      },
      (error: unknown) => {
        if (current) {
          const message = toFailure(error).message
          setSettled((previous) =>
            previous !== null && previous.key === key && 'value' in previous ? previous : { key, attempt, error: message },
          )
        }
      },
    )
    return () => {
      current = false
    }
  }, [key, attempt])

  let loaded: Loaded<T> = { state: 'loading' }
  if (key !== null && settled !== null && settled.key === key) {
    if ('value' in settled) loaded = { state: 'ready', value: settled.value }
    else if (settled.attempt === attempt) loaded = { state: 'error', message: settled.error }
    // an error from an earlier attempt while the retry runs: keep showing "loading"
  }

  return {
    loaded,
    reload: () => setAttempt((n) => n + 1),
    mutate: (value: T) => {
      if (key === null) return
      setSettled({ key, attempt, value })
      setAttempt((n) => n + 1) // drops any older answer still in flight and refetches the truth
    },
  }
}
