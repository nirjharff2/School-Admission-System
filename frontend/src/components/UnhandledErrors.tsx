import { useEffect, useState } from 'react'
import { ApiError, toFailure } from '../api/client'
import ErrorBanner from './ErrorBanner'

const GENERIC = 'Something went wrong. Please try again, or reload the page if it keeps happening.'

/**
 * React error boundaries only see rendering errors. A promise nobody caught or an exception inside an event handler
 * would otherwise end up in the console only; this shows them as a dismissible banner.
 */
export default function UnhandledErrors() {
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    const onRejection = (event: PromiseRejectionEvent) => {
      setMessage(event.reason instanceof ApiError ? toFailure(event.reason).message : GENERIC)
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

  return message === null ? null : <ErrorBanner message={message} onDismiss={() => setMessage(null)} />
}
