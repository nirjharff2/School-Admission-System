import { useState } from 'react'
import { isValidStudentId, useStudentId } from '../studentId'

/** The identity box at the top of every page: the 17-digit birth registration number, saved in localStorage. */
export default function StudentIdBar() {
  const { studentId, valid, setStudentId } = useStudentId()
  // an id that was already saved and is wrong is flagged at once; a number still being typed is not scolded until it
  // cannot become valid (a non-digit, too long), is complete but wrong, or the box was left
  const [left, setLeft] = useState(() => studentId !== '' && !isValidStudentId(studentId))
  const cannotBeValid = studentId.length > 17 || /[^0-9]/.test(studentId)
  const flagged = studentId !== '' && !valid && (left || cannotBeValid)
  const typing = studentId !== '' && !valid && !flagged

  return (
    <div className="student-bar">
      <label htmlFor="student-id">Student ID (birth registration number, 17 digits)</label>
      <input
        id="student-id"
        type="text"
        inputMode="numeric"
        autoComplete="off"
        placeholder="e.g. 20152691234567890"
        value={studentId}
        onChange={(event) => setStudentId(event.target.value)}
        onBlur={() => setLeft(true)}
        onFocus={() => setLeft(false)}
        aria-invalid={flagged}
        aria-describedby="student-id-status"
      />
      <p id="student-id-status" className={flagged ? 'error' : 'hint'} role={flagged ? 'alert' : undefined}>
        {flagged
          ? `Not valid: the number must be exactly 17 digits (0-9). You entered ${studentId.length} character${studentId.length === 1 ? '' : 's'}.`
          : typing
            ? `${studentId.length} of 17 digits so far.`
            : valid
              ? 'Saved on this device. Applying, paying and cancelling use this ID.'
              : 'Enter your ID to apply, pay or cancel.'}
      </p>
    </div>
  )
}
