import { api } from '../api/client'
import StatusBadge from '../components/StatusBadge'
import StudentGate from '../components/StudentGate'
import { formatDateTime } from '../format'
import { useLoad } from '../hooks/useLoad'
import { paths } from '../route'
import { useStudentId } from '../studentId'

/** Every application of the current student, newest first (as the backend returns them), with a link to each. */
export default function MyApplicationsScreen() {
  const { studentId, valid } = useStudentId()
  // the student id is the key: switching to another student shows loading, then that student's list
  const { loaded, reload } = useLoad(valid ? studentId : null, () => api.myHolds(studentId))

  return (
    <section>
      <h1>My Applications</h1>
      <StudentGate>
        {loaded.state === 'loading' && <p className="hint">Loading your applications…</p>}
        {loaded.state === 'error' && (
          <div role="alert">
            <p className="error">Your applications could not be loaded. {loaded.message}</p>
            <button type="button" onClick={reload}>
              Try again
            </button>
          </div>
        )}
        {loaded.state === 'ready' && loaded.value.length === 0 && (
          <p>
            You have not applied to any class yet. <a href={paths.classes}>See the classes</a>.
          </p>
        )}
        {loaded.state === 'ready' && loaded.value.length > 0 && (
          <table>
            <thead>
              <tr>
                <th scope="col">Class</th>
                <th scope="col">Status</th>
                <th scope="col">Expires at</th>
                <th scope="col">
                  <span className="visually-hidden">Details</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {loaded.value.map((hold) => (
                <tr key={hold.holdId}>
                  <th scope="row">{hold.className}</th>
                  <td>
                    <StatusBadge status={hold.status} />
                  </td>
                  <td>{formatDateTime(hold.expiresAt)}</td>
                  <td>
                    <a href={paths.application(hold.holdId)}>Open</a>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </StudentGate>
    </section>
  )
}
