import { useState, type FormEvent } from 'react'
import { api, toFailure, type CreatedClass } from '../api/client'
import { CLASS_NAME_MAX_LENGTH, validateClassForm, type ClassFormErrors } from '../classForm'
import ErrorBanner from '../components/ErrorBanner'
import { paths } from '../route'

/** School admin: create a class (one shift) with a name and a seat count. */
export default function CreateClassScreen() {
  const [name, setName] = useState('')
  const [seats, setSeats] = useState('')
  const [errors, setErrors] = useState<ClassFormErrors>({})
  const [submitting, setSubmitting] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [created, setCreated] = useState<CreatedClass | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (submitting) return
    setCreated(null)
    setFailure(null)
    const result = validateClassForm(name, seats)
    if (!result.ok) {
      setErrors(result.errors)
      return
    }
    setErrors({})
    setSubmitting(true)
    try {
      setCreated(await api.createClass(result.values.name, result.values.totalSeats))
      setName('')
      setSeats('')
    } catch (error) {
      setFailure(toFailure(error).message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section>
      <h1>Create Class</h1>
      <form onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor="class-name">Class name</label>
          <input
            id="class-name"
            type="text"
            autoComplete="off"
            value={name}
            // typing past 200 is allowed so the "at most 200 characters" message can say so, instead of silently cutting the text
            maxLength={CLASS_NAME_MAX_LENGTH + 50}
            onChange={(e) => setName(e.target.value)}
            aria-invalid={errors.name !== undefined}
            aria-describedby={errors.name ? 'class-name-error' : undefined}
            placeholder="e.g. Class 6 - Morning Shift"
          />
          {errors.name && (
            <p id="class-name-error" className="error" role="alert">
              {errors.name}
            </p>
          )}
        </div>
        <div className="field">
          <label htmlFor="class-seats">Total seats</label>
          <input
            id="class-seats"
            type="text"
            inputMode="numeric"
            autoComplete="off"
            value={seats}
            onChange={(e) => setSeats(e.target.value)}
            aria-invalid={errors.totalSeats !== undefined}
            aria-describedby={errors.totalSeats ? 'class-seats-error' : undefined}
            placeholder="e.g. 120"
          />
          {errors.totalSeats && (
            <p id="class-seats-error" className="error" role="alert">
              {errors.totalSeats}
            </p>
          )}
        </div>
        <button type="submit" disabled={submitting}>
          {submitting ? 'Creating…' : 'Create class'}
        </button>
      </form>

      {failure && (
        <ErrorBanner title="The class was not created." message={failure} onDismiss={() => setFailure(null)} />
      )}
      {created && (
        <p className="success" role="status">
          Class “{created.name}” created with {created.totalSeats} seat{created.totalSeats === 1 ? '' : 's'}.{' '}
          <a href={paths.classes}>View the class list</a>
        </p>
      )}
    </section>
  )
}
