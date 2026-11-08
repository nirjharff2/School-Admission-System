import { API_BASE_URL } from '../config'
import { isValidStudentId } from '../studentId'

/** Every failure of an API call (network, 4xx, 5xx, bad payload, missing student id) reaches the UI as this. */
export class ApiError extends Error {
  /** Machine-readable code: the backend's `error` field, or `network_error`, `invalid_response`, `server_error`, `request_failed`. */
  readonly code: string
  /** HTTP status, or null when no response was received. */
  readonly status: number | null

  constructor(code: string, message: string, status: number | null = null) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
  }
}

/** Turns anything thrown by an API call into `{ code, message }` for display. */
export function toFailure(error: unknown): { code: string; message: string } {
  if (error instanceof ApiError) return { code: error.code, message: error.message }
  return { code: 'unexpected_error', message: 'Something went wrong. Please try again.' }
}

export type HoldStatus = 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED'

export interface SchoolClass {
  id: string
  name: string
  totalSeats: number
  heldSeats: number
  confirmedSeats: number
  availableSeats: number
}

export interface CreatedHold {
  holdId: string
  classId: string
  studentId: string
  status: HoldStatus
  expiresAt: string
}

export interface Hold {
  holdId: string
  classId: string
  status: HoldStatus
  expiresAt: string
}

export interface MyHold extends Hold {
  className: string
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'DELETE'
  body?: unknown
  studentId?: string
}

function fallbackMessage(status: number): string {
  if (status >= 500) return 'The server had a problem. Please try again in a moment.'
  if (status === 404) return 'That item could not be found.'
  return 'The request could not be completed.'
}

async function failureFrom(response: Response): Promise<ApiError> {
  let code: string | undefined
  let message: string | undefined
  try {
    const body: unknown = await response.json()
    if (body && typeof body === 'object') {
      const { error, message: text } = body as { error?: unknown; message?: unknown }
      if (typeof error === 'string' && error) code = error
      if (typeof text === 'string' && text) message = text
    }
  } catch {
    // not JSON (a proxy or gateway page): use the generic message below
  }
  return new ApiError(
    code ?? (response.status >= 500 ? 'server_error' : 'request_failed'),
    message ?? fallbackMessage(response.status),
    response.status,
  )
}

async function request<T>(path: string, { method = 'GET', body, studentId }: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (studentId !== undefined) {
    if (!isValidStudentId(studentId)) {
      // never send a request that is certain to be rejected; the UI shows this next to the action
      throw new ApiError('invalid_student_id', 'Enter your 17-digit birth registration number first.')
    }
    headers['X-Student-Id'] = studentId
  }
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  let response: Response
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError(
      'network_error',
      'Cannot reach the server. Check your connection and that the backend is running, then try again.',
    )
  }

  if (!response.ok) throw await failureFrom(response)
  if (response.status === 204) return undefined as T
  try {
    return (await response.json()) as T
  } catch {
    throw new ApiError('invalid_response', 'The server sent an answer that could not be understood.', response.status)
  }
}

const enc = encodeURIComponent

export const api = {
  createClass: (name: string, totalSeats: number) =>
    request<SchoolClass>('/classes', { method: 'POST', body: { name, totalSeats } }),
  listClasses: () => request<SchoolClass[]>('/classes'),
  getClass: (classId: string) => request<SchoolClass>(`/classes/${enc(classId)}`),
  apply: (classId: string, studentId: string) =>
    request<CreatedHold>(`/classes/${enc(classId)}/holds`, { method: 'POST', studentId }),
  getHold: (holdId: string) => request<Hold>(`/holds/${enc(holdId)}`),
  confirm: (holdId: string, studentId: string) =>
    request<{ holdId: string; status: HoldStatus }>(`/holds/${enc(holdId)}/confirm`, { method: 'POST', studentId }),
  cancel: (holdId: string, studentId: string) =>
    request<void>(`/holds/${enc(holdId)}`, { method: 'DELETE', studentId }),
  myHolds: (studentId: string) => request<MyHold[]>('/me/holds', { studentId }),
}
