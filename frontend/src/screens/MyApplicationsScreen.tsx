import { api, type MyHold } from '../api/client'
import ErrorBanner from '../components/ErrorBanner'
import RefreshWarning from '../components/RefreshWarning'
import StatusBadge from '../components/StatusBadge'
import StudentGate from '../components/StudentGate'
import { formatDateTime } from '../format'
import { useNow } from '../hooks/useCountdown'
import { useLoad } from '../hooks/useLoad'
import { effectiveStatus, refreshDelayFor } from '../holdStatus'
import { paths } from '../route'
import { useStudentId } from '../studentId'

/** Seconds between refetches of the list (and sooner when a HELD application is about to expire). */
const REFRESH_MS = 15_000

/** Every application of the current student, newest first (as the backend returns them), with a link to each. */
export default function MyApplicationsScreen() {
  const { studentId, valid } = useStudentId()
  // the student id is the key: switching to another student shows loading, then that student's list
  const { loaded, reload } = useLoad(valid ? studentId : null, () => api.myHolds(studentId), {
    refreshDelay: (holds, now) => refreshDelayFor(holds ?? [], now, REFRESH_MS, REFRESH_MS),
  })

  return (
    <section>
      <h1>My Applications</h1>
      <StudentGate>
        {loaded.state === 'loading' && <p className="hint">Loading your applications…</p>}
        {loaded.state === 'error' && (
          <ErrorBanner title="Your applications could not be loaded." message={loaded.message} onRetry={reload} />
        )}
        {loaded.state === 'ready' && loaded.value.length === 0 && (
          <p>
            You have not applied to any class yet. <a href={paths.classes}>See the classes</a>.
          </p>
        )}
        {loaded.state === 'ready' && loaded.value.length > 0 && (
          <ApplicationsTable holds={loaded.value} requestedAt={loaded.requestedAt} />
        )}
        {loaded.state === 'ready' && loaded.refreshError && (
          <RefreshWarning what="The list" reason={loaded.refreshError} />
        )}
      </StudentGate>
    </section>
  )
}

function ApplicationsTable({ holds, requestedAt }: { holds: MyHold[]; requestedAt: number }) {
  // a HELD application that runs out flips to EXPIRED here without waiting for the next refetch
  const now = useNow(holds.some((h) => h.status === 'HELD'))
  return (
    <table>
      <thead>
        <tr>
          <th scope="col">Class</th>
          <th scope="col">Status</th>
          <th scope="col">Expiry</th>
          <th scope="col">
            <span className="visually-hidden">Details</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {holds.map((hold) => {
          const { status } = effectiveStatus(hold.status, hold.expiresAt, now, requestedAt)
          const showExpiry = status === 'HELD' || status === 'EXPIRED'
          return (
            <tr key={hold.holdId}>
              <th scope="row">{hold.className}</th>
              <td>
                <StatusBadge status={status} />
              </td>
              <td>{showExpiry ? formatDateTime(hold.expiresAt) : '—'}</td>
              <td>
                <a href={paths.application(hold.holdId)}>Open</a>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}
