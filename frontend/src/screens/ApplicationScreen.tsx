import { useState, type FormEvent } from 'react'
import { api, toFailure, type Hold, type HoldStatus } from '../api/client'
import StatusBadge from '../components/StatusBadge'
import { formatDateTime } from '../format'
import { formatRemaining, useCountdown } from '../hooks/useCountdown'
import { useLoad } from '../hooks/useLoad'
import { effectiveStatus, refreshDelayFor } from '../holdStatus'
import { paths } from '../route'
import { useStudentId } from '../studentId'

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

const explanation: Record<HoldStatus, string> = {
  HELD: 'A seat is being held for you. Pay the admission fee before the hold expires to keep it.',
  CONFIRMED: 'Admission confirmed. The fee is paid and the seat is yours.',
  EXPIRED: 'This hold expired and the seat was released. You can apply again from the class list.',
  CANCELLED: 'This application was cancelled and the seat was released.',
}

/** Seconds between status refetches while the hold is HELD (the countdown itself runs locally every second). */
const SLOW_REFRESH_MS = 15_000

interface Detail {
  hold: Hold
  /** Name of the class, or null when it could not be fetched (the class id is shown instead). */
  className: string | null
}

async function loadDetail(holdId: string): Promise<Detail> {
  const hold = await api.getHold(holdId)
  let className: string | null = null
  try {
    className = (await api.getClass(hold.classId)).name
  } catch {
    // the status is what matters on this screen; without the name the class id is shown
  }
  return { hold, className }
}

type Notice = { kind: 'success' | 'error'; text: string }

/** One application: status, live countdown, class, and the Pay / Cancel actions (enabled only while the hold is HELD). */
export default function ApplicationScreen({ holdId }: { holdId: string }) {
  const { studentId, valid } = useStudentId()
  const { loaded, reload, mutate } = useLoad(holdId, () => loadDetail(holdId), {
    // a confirmed, expired or cancelled application can no longer change, so only a HELD one is refetched on a timer
    refreshDelay: (detail, now) => (detail ? refreshDelayFor([detail.hold], now, SLOW_REFRESH_MS, null) : SLOW_REFRESH_MS),
  })
  const [busy, setBusy] = useState<'pay' | 'cancel' | null>(null)
  const [askingToCancel, setAskingToCancel] = useState(false)
  const [notice, setNotice] = useState<Notice | null>(null)

  async function act(kind: 'pay' | 'cancel', detail: Detail) {
    if (busy) return
    setBusy(kind)
    setNotice(null)
    setAskingToCancel(false)
    try {
      if (kind === 'pay') {
        const result = await api.confirm(holdId, studentId)
        mutate({ ...detail, hold: { ...detail.hold, status: result.status } })
        setNotice({ kind: 'success', text: 'Payment received. Your admission is confirmed.' })
      } else {
        await api.cancel(holdId, studentId)
        mutate({ ...detail, hold: { ...detail.hold, status: 'CANCELLED' } })
        setNotice({ kind: 'success', text: 'Application cancelled. The seat has been released.' })
      }
    } catch (error) {
      setNotice({ kind: 'error', text: toFailure(error).message })
      reload() // the status shown may be out of date (for example the hold just expired)
    } finally {
      setBusy(null)
    }
  }

  return (
    <section>
      <h1>Application</h1>

      {loaded.state === 'loading' && <p className="hint">Loading application…</p>}
      {loaded.state === 'error' && (
        <div role="alert">
          <p className="error">This application could not be loaded. {loaded.message}</p>
          <button type="button" onClick={reload}>
            Try again
          </button>{' '}
          <a href={paths.applicationLookup}>Open another application</a>
        </div>
      )}

      {loaded.state === 'ready' && (
        <>
          <ApplicationDetails
            detail={loaded.value}
            requestedAt={loaded.requestedAt}
            valid={valid}
            busy={busy}
            askingToCancel={askingToCancel}
            onPay={() => void act('pay', loaded.value)}
            onAskToCancel={() => setAskingToCancel(true)}
            onKeep={() => setAskingToCancel(false)}
            onCancel={() => void act('cancel', loaded.value)}
          />
          {loaded.refreshError && (
            <p className="hint">
              The latest status could not be fetched ({loaded.refreshError}). Showing the last known status; trying again
              automatically.
            </p>
          )}
        </>
      )}

      {notice && (
        <p className={notice.kind === 'error' ? 'error' : 'success'} role={notice.kind === 'error' ? 'alert' : 'status'}>
          {notice.text}
        </p>
      )}

      <p>
        <a href={paths.myApplications}>My applications</a> · <a href={paths.classes}>Classes</a>
      </p>
    </section>
  )
}

interface DetailsProps {
  detail: Detail
  requestedAt: number
  valid: boolean
  busy: 'pay' | 'cancel' | null
  askingToCancel: boolean
  onPay: () => void
  onAskToCancel: () => void
  onKeep: () => void
  onCancel: () => void
}

function ApplicationDetails({ detail, requestedAt, valid, busy, askingToCancel, onPay, onAskToCancel, onKeep, onCancel }: DetailsProps) {
  const { hold, className } = detail
  const { now, remainingMs } = useCountdown(hold.expiresAt, hold.status === 'HELD')
  const { status, serverDisagrees } = effectiveStatus(hold.status, hold.expiresAt, now, requestedAt)
  const held = status === 'HELD'
  const reason = !held
    ? 'Only an application that is still on hold can be paid or cancelled.'
    : !valid
      ? 'Enter a valid 17-digit student ID first.'
      : null
  const disabled = reason !== null || busy !== null

  return (
    <>
      <dl>
        <dt>Class</dt>
        <dd>{className ?? hold.classId}</dd>
        <dt>Status</dt>
        <dd>
          <StatusBadge status={status} />
        </dd>
        {held && remainingMs !== null && (
          <>
            <dt>Time left</dt>
            <dd>
              {/* role=timer is not announced on every tick; the status text below announces the change to EXPIRED */}
              <span role="timer" className="countdown">
                {formatRemaining(remainingMs)}
              </span>
              {serverDisagrees && <span className="hint"> Confirming with the server…</span>}
            </dd>
          </>
        )}
        {(status === 'HELD' || status === 'EXPIRED') && (
          <>
            <dt>{status === 'HELD' ? 'Expires at' : 'Expired at'}</dt>
            <dd>{formatDateTime(hold.expiresAt)}</dd>
          </>
        )}
        <dt>Application ID</dt>
        <dd>
          <code>{hold.holdId}</code>
        </dd>
      </dl>
      <p aria-live="polite">{explanation[status]}</p>

      {askingToCancel && held ? (
        <p role="alertdialog" aria-label="Confirm cancellation">
          Cancel this application and release the seat?{' '}
          <button type="button" onClick={onCancel} disabled={busy !== null}>
            Yes, cancel it
          </button>{' '}
          <button type="button" onClick={onKeep}>
            Keep it
          </button>
        </p>
      ) : (
        <p>
          <button type="button" onClick={onPay} disabled={disabled} title={reason ?? undefined}>
            {busy === 'pay' ? 'Paying…' : 'Pay admission fee (ভর্তি ফি দিন)'}
          </button>{' '}
          <button type="button" onClick={onAskToCancel} disabled={disabled} title={reason ?? undefined}>
            {busy === 'cancel' ? 'Cancelling…' : 'Cancel (বাতিল)'}
          </button>
        </p>
      )}
      {held && !valid && <p className="hint">{reason}</p>}
    </>
  )
}
