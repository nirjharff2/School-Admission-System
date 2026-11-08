/** Local, human-readable date and time for an ISO-8601 timestamp; the raw text if it cannot be parsed. */
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}
