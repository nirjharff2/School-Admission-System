import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import type * as Client from '../api/client'
import { setStudentId } from '../studentId'
import ClassListScreen from './ClassListScreen'

// every API call is replaced; the real error classes stay so messages and codes behave as in the app
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof Client>()
  return { ...actual, api: { listClasses: vi.fn(), apply: vi.fn() } }
})

const STUDENT = '20152691234567890'

const classes: Client.SchoolClass[] = [
  { id: 'c_open', name: 'Open class', totalSeats: 10, heldSeats: 2, confirmedSeats: 1, availableSeats: 7 },
  { id: 'c_full', name: 'Full class', totalSeats: 5, heldSeats: 3, confirmedSeats: 2, availableSeats: 0 },
]

async function rowOf(name: string) {
  return within(await screen.findByRole('row', { name: new RegExp(name) }))
}

beforeEach(() => {
  vi.mocked(api.listClasses).mockResolvedValue(classes)
})

describe('the class list', () => {
  it('shows every seat count of a class', async () => {
    render(<ClassListScreen />)

    const row = await rowOf('Open class')
    expect(row.getAllByRole('cell').map((c) => c.textContent).slice(0, 4)).toEqual(['10', '2', '1', '7'])
  })

  it('disables Apply for a class with no available seats, and enables it for one with seats', async () => {
    setStudentId(STUDENT)
    render(<ClassListScreen />)

    expect((await rowOf('Full class')).getByRole('button')).toBeDisabled()
    expect((await rowOf('Full class')).getByText('Full')).toBeInTheDocument()
    expect((await rowOf('Open class')).getByRole('button')).toBeEnabled()
  })

  it('disables Apply everywhere while the student id is invalid, and says why', async () => {
    setStudentId('2015')
    render(<ClassListScreen />)

    expect((await rowOf('Open class')).getByRole('button')).toBeDisabled()
    expect(screen.getByText(/Enter a valid 17-digit student ID/)).toBeInTheDocument()
  })

  it('applies with the saved student id and opens the new application', async () => {
    setStudentId(STUDENT)
    vi.mocked(api.apply).mockResolvedValue({ holdId: 'h_9', classId: 'c_open', studentId: STUDENT, status: 'HELD', expiresAt: '2026-11-01T04:10:00Z' })
    render(<ClassListScreen />)

    fireEvent.click((await rowOf('Open class')).getByRole('button'))

    await vi.waitFor(() => expect(window.location.hash).toBe('#/application/h_9'))
    expect(api.apply).toHaveBeenCalledWith('c_open', STUDENT)
  })

  it('shows the backend message when the last seat went to someone else (409) and stays on the list', async () => {
    setStudentId(STUDENT)
    vi.mocked(api.apply).mockRejectedValue(new ApiError('no_seats_available', 'No seats are left in this class.', 409))
    render(<ClassListScreen />)

    fireEvent.click((await rowOf('Open class')).getByRole('button'))

    expect(await screen.findByRole('alert')).toHaveTextContent('No seats are left in this class.')
    expect(window.location.hash).toBe('')
  })

  it('shows a readable message with a retry when the list cannot be loaded', async () => {
    vi.mocked(api.listClasses).mockRejectedValue(new ApiError('network_error', 'Cannot reach the server. Check your connection.'))
    render(<ClassListScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot reach the server.')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
