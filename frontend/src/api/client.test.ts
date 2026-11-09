import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, isPermanentFailure, toFailure } from './client'

const fetchMock = vi.fn<typeof fetch>()
const STUDENT = '20152691234567890'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

async function failureOf(call: Promise<unknown>): Promise<ApiError> {
  const error = await call.then(
    () => undefined,
    (e: unknown) => e,
  )
  expect(error).toBeInstanceOf(ApiError)
  return error as ApiError
}

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('failures reach the UI as { code, message }', () => {
  it('turns a rejected fetch (backend down) into a readable network_error', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    const failure = await failureOf(api.listClasses())

    expect(toFailure(failure)).toEqual({
      code: 'network_error',
      message: expect.stringContaining('Cannot reach the server'),
    })
    expect(failure.status).toBeNull()
  })

  it('keeps the backend code and message for a 409', async () => {
    fetchMock.mockResolvedValue(jsonResponse(409, { error: 'no_seats_available', message: 'No seats are left in this class.' }))

    const failure = await failureOf(api.apply('c_1', STUDENT))

    expect(toFailure(failure)).toEqual({ code: 'no_seats_available', message: 'No seats are left in this class.' })
    expect(failure.status).toBe(409)
  })

  it('does not show a gateway page: a non-JSON 502 becomes a generic sentence', async () => {
    fetchMock.mockResolvedValue(new Response('<html>Bad gateway</html>', { status: 502 }))

    const failure = await failureOf(api.getHold('h_1'))

    expect(failure.code).toBe('server_error')
    expect(failure.message).not.toContain('<html>')
    expect(failure.message).toMatch(/server had a problem/i)
  })

  it('reports a request that took too long as a timeout', async () => {
    fetchMock.mockRejectedValue(new DOMException('signal timed out', 'TimeoutError'))

    const failure = await failureOf(api.getClass('c_1'))

    expect(failure.code).toBe('timeout')
    expect(failure.message).toMatch(/too long/i)
  })

  it('reports a success status with an unreadable body as invalid_response', async () => {
    fetchMock.mockResolvedValue(new Response('<html>oops</html>', { status: 200 }))

    expect((await failureOf(api.listClasses())).code).toBe('invalid_response')
  })

  it('tells an unexpected error apart from an API error', () => {
    expect(toFailure(new Error('boom'))).toEqual({ code: 'unexpected_error', message: expect.any(String) })
  })
})

describe('student identity', () => {
  it('sends X-Student-Id on student calls and nothing on the public ones', async () => {
    fetchMock.mockImplementation(async () => jsonResponse(200, []))

    await api.myHolds(STUDENT)
    await api.listClasses()

    const [studentCall, publicCall] = fetchMock.mock.calls
    expect(new Headers(studentCall[1]?.headers).get('X-Student-Id')).toBe(STUDENT)
    expect(new Headers(publicCall[1]?.headers).get('X-Student-Id')).toBeNull()
  })

  it('refuses to send a student call with an invalid id, without touching the network', async () => {
    const failure = await failureOf(api.apply('c_1', '123'))

    expect(failure.code).toBe('invalid_student_id')
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('success shapes', () => {
  it('resolves a 204 (cancel) without trying to read a body', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }))

    await expect(api.cancel('h_1', STUDENT)).resolves.toBeUndefined()
  })
})

describe('isPermanentFailure', () => {
  it.each([
    [404, true],
    [409, true],
    [400, true],
    [408, false],
    [429, false],
    [503, false],
    [500, false],
  ])('status %i -> permanent %s', (status, permanent) => {
    expect(isPermanentFailure(new ApiError('x', 'y', status))).toBe(permanent)
  })

  it('treats a network failure as worth retrying', () => {
    expect(isPermanentFailure(new ApiError('network_error', 'down'))).toBe(false)
  })
})
