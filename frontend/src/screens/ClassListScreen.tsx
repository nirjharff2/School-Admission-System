import { useState } from 'react'
import { api, toFailure } from '../api/client'
import ErrorBanner from '../components/ErrorBanner'
import RefreshWarning from '../components/RefreshWarning'
import { useLoad } from '../hooks/useLoad'
import { paths } from '../route'
import { useStudentId } from '../studentId'

/** Seconds between refetches of the seat counts: a seat taken or released elsewhere shows up within this time. */
const REFRESH_MS = 4_000

export default function ClassListScreen() {
  const { loaded, reload } = useLoad('classes', () => api.listClasses(), { refreshDelay: () => REFRESH_MS })
  const { studentId, valid } = useStudentId()
  const [applyingTo, setApplyingTo] = useState<string | null>(null)
  // remembered with the student it was for, so it disappears when another student id is entered
  const [applyFailure, setApplyFailure] = useState<{ studentId: string; message: string } | null>(null)

  async function apply(classId: string) {
    if (applyingTo) return
    setApplyingTo(classId)
    setApplyFailure(null)
    try {
      const hold = await api.apply(classId, studentId)
      window.location.hash = paths.application(hold.holdId)
    } catch (error) {
      setApplyFailure({ studentId, message: toFailure(error).message })
      reload() // the seat counts shown were stale (for example the last seat just went)
    } finally {
      setApplyingTo(null)
    }
  }

  return (
    <section>
      <h1>Classes</h1>
      {!valid && <p className="hint">Enter a valid 17-digit student ID at the top of the page to apply for a seat.</p>}
      {applyFailure?.studentId === studentId && (
        <ErrorBanner title="Your application was not made." message={applyFailure.message} />
      )}

      {loaded.state === 'loading' && <p className="hint">Loading classes…</p>}
      {loaded.state === 'error' && (
        <ErrorBanner title="The class list could not be loaded." message={loaded.message} onRetry={reload} />
      )}
      {loaded.state === 'ready' && loaded.refreshError && (
        <RefreshWarning what="The seat numbers" reason={loaded.refreshError} />
      )}
      {loaded.state === 'ready' && loaded.value.length === 0 && (
        <p>
          There are no classes yet. <a href={paths.createClass}>Create the first class</a>.
        </p>
      )}
      {loaded.state === 'ready' && loaded.value.length > 0 && (
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
            {loaded.value.map((c) => {
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
