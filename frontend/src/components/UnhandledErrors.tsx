import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import ErrorBanner from './ErrorBanner'

const GENERIC = 'Please try again, or reload the page if it keeps happening.'

/**
 * React error boundaries only see rendering errors. A promise nobody caught or an exception inside an event handler
 * would otherwise end up in the console only; this shows them as a dismissible banner. Mount it with a `key` that
 * changes with the screen so a banner about one screen does not follow the user to the next.
 */
export default function UnhandledErrors() {
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    const onRejection = (event: PromiseRejectionEvent) => {
      setMessage(event.reason instanceof ApiError ? event.reason.message : GENERIC)
    }
    const onError = (event: ErrorEvent) => {
      // resource load failures also fire "error" but carry no exception: not an application error
      if (event.error) setMessage(event.error instanceof ApiError ? event.error.message : GENERIC)
    }
    window.addEventListener('unhandledrejection', onRejection)
    window.addEventListener('error', onError)
    return () => {
      window.removeEventListener('unhandledrejection', onRejection)
      window.removeEventListener('error', onError)
    }
  }, [])

  return message === null ? null : (
    <ErrorBanner title="Something went wrong." message={message} onDismiss={() => setMessage(null)} />
  )
}
