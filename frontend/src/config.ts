const DEFAULT_API_BASE_URL = 'http://localhost:8080'

/** Backend base URL from `VITE_API_BASE_URL` (set at build time), without trailing slashes; blank means the default. */
export function resolveApiBaseUrl(value: string | undefined): string {
  const trimmed = value?.trim().replace(/\/+$/, '')
  return trimmed ? trimmed : DEFAULT_API_BASE_URL
}

export const API_BASE_URL = resolveApiBaseUrl(import.meta.env.VITE_API_BASE_URL)
