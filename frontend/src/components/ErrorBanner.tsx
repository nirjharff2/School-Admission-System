import type { ReactNode } from 'react'

interface Props {
  /** What failed, in a few words ("The class was not created."); shown in bold before the message. */
  title?: string
  /** Why, in words for the user: the backend's message or one of the client's own messages. */
  message: string
  onRetry?: () => void
  onDismiss?: () => void
  /** Extra links or buttons shown after the message. */
  children?: ReactNode
}

/** The one place errors are shown to the user: a visible, announced (role=alert) message, never console-only. */
export default function ErrorBanner({ title, message, onRetry, onDismiss, children }: Props) {
  return (
    <div className="banner" role="alert">
      <p>
        {title && <strong>{title} </strong>}
        {message}
      </p>
      {(onRetry || onDismiss || children) && (
        <p className="banner-actions">
          {onRetry && (
            <button type="button" onClick={onRetry}>
              Try again
            </button>
          )}
          {onDismiss && (
            <button type="button" onClick={onDismiss}>
              Dismiss
            </button>
          )}
          {children}
        </p>
      )}
    </div>
  )
}
