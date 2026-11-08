import type { ReactNode } from 'react'
import { useStudentId } from '../studentId'

/** Renders its children only while the student id is valid; otherwise says why student actions are unavailable. */
export default function StudentGate({ children }: { children: ReactNode }) {
  const { valid } = useStudentId()
  if (valid) return children
  return (
    <p className="error" role="alert">
      Enter a valid 17-digit student ID in the box at the top of the page to continue.
    </p>
  )
}
