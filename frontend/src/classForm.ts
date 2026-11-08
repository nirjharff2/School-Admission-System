/** Mirrors the backend's rules for `POST /classes` so mistakes are shown before anything is sent. */
export const CLASS_NAME_MAX_LENGTH = 200
export const MAX_TOTAL_SEATS = 2_147_483_647 // the backend stores seats in a 32-bit integer

export interface ClassFormErrors {
  name?: string
  totalSeats?: string
}

export interface ClassFormValues {
  name: string
  totalSeats: number
}

function hasControlCharacter(text: string): boolean {
  return [...text].some((ch) => {
    const code = ch.codePointAt(0)!
    return code < 0x20 || (code >= 0x7f && code <= 0x9f)
  })
}

export type ClassFormResult = { ok: true; values: ClassFormValues } | { ok: false; errors: ClassFormErrors }

export function validateClassForm(rawName: string, rawSeats: string): ClassFormResult {
  const errors: ClassFormErrors = {}

  const name = rawName.trim()
  if (!name) errors.name = 'Enter a class name.'
  else if (name.length > CLASS_NAME_MAX_LENGTH) errors.name = `The name can be at most ${CLASS_NAME_MAX_LENGTH} characters.`
  else if (hasControlCharacter(name)) errors.name = 'The name cannot contain control characters.'

  const seatsText = rawSeats.trim()
  let totalSeats = 0
  if (!seatsText) errors.totalSeats = 'Enter the number of seats.'
  else if (!/^[0-9]+$/.test(seatsText)) errors.totalSeats = 'Seats must be a whole number such as 120.'
  else {
    totalSeats = Number(seatsText)
    if (totalSeats < 1) errors.totalSeats = 'A class needs at least 1 seat.'
    else if (totalSeats > MAX_TOTAL_SEATS) errors.totalSeats = `Seats cannot be more than ${MAX_TOTAL_SEATS.toLocaleString('en')}.`
  }

  return errors.name || errors.totalSeats ? { ok: false, errors } : { ok: true, values: { name, totalSeats } }
}
