import { Component, type ErrorInfo, type ReactNode } from 'react'

interface Props {
  children: ReactNode
  /** When this value changes (for example the route), a boundary that has failed tries rendering its children again. */
  resetKey?: string
}

interface State {
  failed: boolean
  resetKey: string | undefined
}

/** Last line of defence: a rendering error shows a readable message and a way out instead of a blank page. */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { failed: false, resetKey: this.props.resetKey }

  static getDerivedStateFromError(): Partial<State> {
    return { failed: true }
  }

  static getDerivedStateFromProps(props: Props, state: State): Partial<State> | null {
    return props.resetKey === state.resetKey ? null : { failed: false, resetKey: props.resetKey }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Unhandled UI error', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return (
      <div className="boundary" role="alert">
        <h1>Something went wrong</h1>
        <p>
          The page hit an unexpected problem. Your applications are safe on the server. Please try again, or reload the
          page.
        </p>
        <button type="button" onClick={() => this.setState({ failed: false })}>
          Try again
        </button>{' '}
        <button type="button" onClick={() => window.location.reload()}>
          Reload page
        </button>
      </div>
    )
  }
}
