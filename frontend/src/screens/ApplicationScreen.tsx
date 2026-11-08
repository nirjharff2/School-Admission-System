import { useState, type FormEvent } from 'react'
import StudentGate from '../components/StudentGate'
import { paths } from '../route'

/** Opens one application by its hold id (the id the backend returned when the seat was held). */
export function ApplicationLookup() {
  const [holdId, setHoldId] = useState('')

  function open(event: FormEvent) {
    event.preventDefault()
    const id = holdId.trim()
    if (id) window.location.hash = paths.application(id)
  }

  return (
    <section>
      <h1>Application</h1>
      <form onSubmit={open}>
        <label htmlFor="hold-id">Application (hold) ID</label>
        <input id="hold-id" type="text" autoComplete="off" value={holdId} onChange={(e) => setHoldId(e.target.value)} />{' '}
        <button type="submit" disabled={!holdId.trim()}>
          Open
        </button>
      </form>
    </section>
  )
}

export default function ApplicationScreen({ holdId }: { holdId: string }) {
  return (
    <section>
      <h1>Application</h1>
      <p>
        Application ID: <code>{holdId}</code>
      </p>
      <StudentGate>
        <p className="hint">Status, countdown, pay and cancel are added in a later step.</p>
      </StudentGate>
    </section>
  )
}
