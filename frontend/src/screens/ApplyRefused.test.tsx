import { fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { setStudentId } from '../studentId'
import ClassListScreen from './ClassListScreen'

// This file deliberately does NOT mock the API client: a real 409 response body goes through the real client
// (fetch is the only thing replaced) and must end up as readable text on the screen.

const STUDENT = '20152691234567890'
const fetchMock = vi.fn<typeof fetch>()

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
  setStudentId(STUDENT)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('applying when the backend says no', () => {
  it('shows the backend message from a 409 response body', async () => {
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input)
      if (init?.method === 'POST' && url.endsWith('/classes/c_1/holds')) {
        return new Response(JSON.stringify({ error: 'no_seats_available', message: 'No seats are left in this class.' }), {
          status: 409,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(
        JSON.stringify([{ id: 'c_1', name: 'Class 6', totalSeats: 5, heldSeats: 4, confirmedSeats: 0, availableSeats: 1 }]),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })
    render(<ClassListScreen />)

    fireEvent.click(within(await screen.findByRole('row', { name: /Class 6/ })).getByRole('button'))

    expect(await screen.findByRole('alert')).toHaveTextContent('No seats are left in this class.')
    const apply = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(new Headers(apply?.[1]?.headers).get('X-Student-Id')).toBe(STUDENT)
  })
})
