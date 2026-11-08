import { useCallback, useEffect, useState } from 'react'
import { api, toFailure, type SchoolClass } from '../api/client'
import { paths } from '../route'
import { useStudentId } from '../studentId'

type Loaded = { state: 'loading' } | { state: 'error'; message: string } | { state: 'ready'; classes: SchoolClass[] }

/** Loads the class list. `refresh` refetches quietly; `retry` shows the loading state again first. */
function useClasses() {
  const [loaded, setLoaded] = useState<Loaded>({ state: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let current = true // false once a newer request started or the screen closed: its answer is dropped
    api.listClasses().then(
      (classes) => {
        if (current) setLoaded({ state: 'ready', classes })
      },
      (error: unknown) => {
        // a failed refresh keeps the last good list on screen; only a first load shows the error
        if (current) {
          setLoaded((previous) =>
            previous.state === 'ready' ? previous : { state: 'error', message: toFailure(error).message },
          )
        }
      },
    )
    return () => {
      current = false
    }
  }, [attempt])

  const refresh = useCallback(() => setAttempt((n) => n + 1), [])
  const retry = useCallback(() => {
    setLoaded({ state: 'loading' })
    setAttempt((n) => n + 1)
  }, [])

  return { loaded, refresh, retry }
}

export default function ClassListScreen() {
  const { loaded, refresh, retry } = useClasses()
  const { studentId, valid } = useStudentId()
  const [applyingTo, setApplyingTo] = useState<string | null>(null)
  const [applyFailure, setApplyFailure] = useState<string | null>(null)

  async function apply(classId: string) {
    if (applyingTo) return
    setApplyingTo(classId)
    setApplyFailure(null)
    try {
      const hold = await api.apply(classId, studentId)
      window.location.hash = paths.application(hold.holdId)
    } catch (error) {
      setApplyFailure(toFailure(error).message)
      refresh() // the seat counts shown were stale (for example the last seat just went)
    } finally {
      setApplyingTo(null)
    }
  }

  return (
    <section>
      <h1>Classes</h1>
      {!valid && <p className="hint">Enter a valid 17-digit student ID at the top of the page to apply for a seat.</p>}
      {applyFailure && (
        <p className="error" role="alert">
          {applyFailure}
        </p>
      )}

      {loaded.state === 'loading' && <p className="hint">Loading classes…</p>}
      {loaded.state === 'error' && (
        <div role="alert">
          <p className="error">The class list could not be loaded. {loaded.message}</p>
          <button type="button" onClick={retry}>
            Try again
          </button>
        </div>
      )}
      {loaded.state === 'ready' && loaded.classes.length === 0 && (
        <p>
          There are no classes yet. <a href={paths.createClass}>Create the first class</a>.
        </p>
      )}
      {loaded.state === 'ready' && loaded.classes.length > 0 && (
        <table>
          <thead>
            <tr>
              <th scope="col">Class</th>
              <th scope="col">Total</th>
              <th scope="col">Held</th>
              <th scope="col">Confirmed</th>
              <th scope="col">Available</th>
              <th scope="col">
                <span className="visually-hidden">Apply</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {loaded.classes.map((c) => {
              const full = c.availableSeats <= 0
              const disabledReason = full
                ? 'No seats are available in this class.'
                : !valid
                  ? 'Enter a valid 17-digit student ID first.'
                  : null
              return (
                <tr key={c.id}>
                  <th scope="row">{c.name}</th>
                  <td>{c.totalSeats}</td>
                  <td>{c.heldSeats}</td>
                  <td>{c.confirmedSeats}</td>
                  <td>{c.availableSeats}</td>
                  <td>
                    <button
                      type="button"
                      onClick={() => void apply(c.id)}
                      disabled={disabledReason !== null || applyingTo !== null}
                      title={disabledReason ?? undefined}
                    >
                      {applyingTo === c.id ? 'Applying…' : 'Apply (আবেদন করুন)'}
                    </button>
                    {full && <span className="hint"> Full</span>}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}
