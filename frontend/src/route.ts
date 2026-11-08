import { useSyncExternalStore } from 'react'

export type Route =
  | { name: 'create-class' }
  | { name: 'classes' }
  | { name: 'application-lookup' }
  | { name: 'application'; holdId: string }
  | { name: 'my-applications' }
  | { name: 'not-found' }

export const paths = {
  createClass: '#/classes/new',
  classes: '#/classes',
  applicationLookup: '#/application',
  application: (holdId: string) => `#/application/${encodeURIComponent(holdId)}`,
  myApplications: '#/my-applications',
}

/** Hash routing: no server rewrite rules needed, and a refresh stays on the same screen. */
export function parseRoute(hash: string): Route {
  const path = hash.replace(/^#/, '').replace(/\/+$/, '') || '/classes'
  if (path === '/' || path === '/classes') return { name: 'classes' }
  if (path === '/classes/new') return { name: 'create-class' }
  if (path === '/my-applications') return { name: 'my-applications' }
  if (path === '/application') return { name: 'application-lookup' }
  const match = /^\/application\/([^/]+)$/.exec(path)
  if (match) {
    try {
      return { name: 'application', holdId: decodeURIComponent(match[1]) }
    } catch {
      return { name: 'not-found' }
    }
  }
  return { name: 'not-found' }
}

function subscribe(listener: () => void) {
  window.addEventListener('hashchange', listener)
  return () => window.removeEventListener('hashchange', listener)
}

export function useRoute(): Route {
  // the snapshot is the (immutable) hash string; parsing happens per render
  return parseRoute(useSyncExternalStore(subscribe, () => window.location.hash))
}
