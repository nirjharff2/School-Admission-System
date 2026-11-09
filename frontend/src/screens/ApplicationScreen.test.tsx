import { act, fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, type HoldStatus } from '../api/client'
import type * as Client from '../api/client'
import { setStudentId } from '../studentId'
import ApplicationScreen from './ApplicationScreen'

// every API call is replaced; the real error classes stay so messages and codes behave as in the app
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof Client>()
  return { ...actual, api: { getHold: vi.fn(), getClass: vi.fn(), confirm: vi.fn(), cancel: vi.fn() } }
})

const STUDENT = '20152691234567890'
const EXPIRES_AT = '2026-11-01T04:10:00Z'

/** The fake server: one hold whose status the tests can change. */
let status: HoldStatus

function payButton() {
  return screen.getByRole('button', { name: /Pay admission fee/ })
}

function cancelButton() {
  return screen.getByRole('button', { name: /^Cancel/ })
}

async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0)
  })
}

beforeEach(() => {
  vi.useFakeTimers({ now: new Date('2026-11-01T04:09:57Z'), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] })
  status = 'HELD'
  setStudentId(STUDENT)
  vi.mocked(api.getHold).mockImplementation(async () => ({ holdId: 'h_1', classId: 'c_1', status, expiresAt: EXPIRES_AT }))
  vi.mocked(api.getClass).mockResolvedValue({
    id: 'c_1',
    name: 'Class 6 - Morning Shift',
    totalSeats: 10,
    heldSeats: 1,
    confirmedSeats: 0,
    availableSeats: 9,
  })
  vi.mocked(api.confirm).mockImplementation(async () => {
    status = 'CONFIRMED'
    return { holdId: 'h_1', status }
  })
})

describe('the application screen', () => {
  it('counts down and, when the time runs out, shows EXPIRED and disables Pay and Cancel without a reload', async () => {
    render(<ApplicationScreen holdId="h_1" />)
    await settle()

    expect(screen.getByText('Class 6 - Morning Shift')).toBeInTheDocument()
    expect(screen.getByText('HELD')).toBeInTheDocument()
    expect(screen.getByRole('timer')).toHaveTextContent('0:03')
    expect(payButton()).toBeEnabled()

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000)
    })
    expect(screen.getByRole('timer')).toHaveTextContent('0:02')
    expect(payButton()).toBeEnabled()

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000) // the clock reaches expiresAt
    })
    expect(screen.getByText('EXPIRED')).toBeInTheDocument()
    expect(screen.queryByText('HELD')).not.toBeInTheDocument()
    expect(payButton()).toBeDisabled()
    expect(cancelButton()).toBeDisabled()
  })

  it('lets the server overrule a browser clock that runs ahead: still HELD after expiresAt means keep waiting for it', async () => {
    render(<ApplicationScreen holdId="h_1" />)
    await settle()

    // the refetch that follows the local expiry (sent after expiresAt) still answers HELD
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000 + 600)
    })

    expect(screen.getByText('HELD')).toBeInTheDocument()
    expect(screen.getByText(/Confirming with the server/)).toBeInTheDocument()
    expect(payButton()).toBeEnabled()
  })

  it('pays: the status becomes CONFIRMED and both buttons are disabled', async () => {
    render(<ApplicationScreen holdId="h_1" />)
    await settle()

    fireEvent.click(payButton())
    await settle()

    expect(api.confirm).toHaveBeenCalledWith('h_1', STUDENT)
    expect(screen.getByText('CONFIRMED')).toBeInTheDocument()
    expect(screen.getByText(/Payment received/)).toBeInTheDocument()
    expect(payButton()).toBeDisabled()
    expect(cancelButton()).toBeDisabled()
  })

  it('shows the backend message when paying is refused with a 409', async () => {
    vi.mocked(api.confirm).mockRejectedValue(
      new ApiError('hold_expired', 'This application has expired, so the fee can no longer be paid. Please apply again.', 409),
    )
    render(<ApplicationScreen holdId="h_1" />)
    await settle()

    fireEvent.click(payButton())
    await settle()

    expect(screen.getByRole('alert')).toHaveTextContent('This application has expired, so the fee can no longer be paid.')
    expect(screen.getByRole('alert')).toHaveTextContent('The fee was not paid.')
  })

  it('disables Pay and Cancel while the student id is invalid', async () => {
    setStudentId('123')
    render(<ApplicationScreen holdId="h_1" />)
    await settle()

    expect(payButton()).toBeDisabled()
    expect(cancelButton()).toBeDisabled()
    expect(screen.getAllByText(/Enter a valid 17-digit student ID first/).length).toBeGreaterThan(0)
  })
})
