import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'
import { setStudentId } from '../studentId'

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  setStudentId('') // the id is module state shared by every test in a file
  localStorage.clear()
  window.location.hash = ''
})
