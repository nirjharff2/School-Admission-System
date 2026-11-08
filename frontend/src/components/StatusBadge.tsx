import type { HoldStatus } from '../api/client'

/** The application status exactly as the API reports it (HELD, CONFIRMED, EXPIRED, CANCELLED), coloured per state. */
export default function StatusBadge({ status }: { status: HoldStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>
}
