import { act, fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { getStudentId, setStudentId } from '../studentId'
import StudentIdBar from './StudentIdBar'

const STORAGE_KEY = 'admission.studentId'
const VALID = '20152691234567890'

function box() {
  return screen.getByLabelText(/Student ID/)
}

describe('the student id box', () => {
  it('saves what the user types to localStorage and clears the key when emptied', () => {
    render(<StudentIdBar />)

    fireEvent.change(box(), { target: { value: VALID } })
    expect(localStorage.getItem(STORAGE_KEY)).toBe(VALID)
    expect(getStudentId()).toBe(VALID)
    expect(screen.getByText(/Saved on this device/)).toBeInTheDocument()

    fireEvent.change(box(), { target: { value: '' } })
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull()
  })

  it('shows a saved id when the page opens', () => {
    setStudentId(VALID)

    render(<StudentIdBar />)

    expect(box()).toHaveValue(VALID)
    expect(box()).toHaveAttribute('aria-invalid', 'false')
  })

  it('does not scold a number that is still being typed, but counts the digits', () => {
    render(<StudentIdBar />)

    fireEvent.change(box(), { target: { value: '2015' } })

    expect(box()).toHaveAttribute('aria-invalid', 'false')
    expect(screen.getByText('4 of 17 digits so far.')).toBeInTheDocument()
  })

  it.each([
    ['a letter', '2015a'],
    ['more than 17 digits', '201526912345678901'],
  ])('flags %s at once, and says what is wrong', (_label, value) => {
    render(<StudentIdBar />)

    fireEvent.change(box(), { target: { value } })

    expect(box()).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('alert')).toHaveTextContent('exactly 17 digits')
  })

  it('flags an incomplete id once the box is left', () => {
    render(<StudentIdBar />)
    fireEvent.change(box(), { target: { value: '2015' } })

    fireEvent.blur(box())

    expect(box()).toHaveAttribute('aria-invalid', 'true')
  })

  it('follows a change made in another tab', () => {
    render(<StudentIdBar />)

    act(() => {
      localStorage.setItem(STORAGE_KEY, VALID)
      window.dispatchEvent(new StorageEvent('storage', { key: STORAGE_KEY, newValue: VALID }))
    })

    expect(box()).toHaveValue(VALID)
  })
})
