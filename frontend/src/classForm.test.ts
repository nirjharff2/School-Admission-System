import { describe, expect, it } from 'vitest'
import { MAX_TOTAL_SEATS, validateClassForm } from './classForm'

describe('validateClassForm', () => {
  it('accepts a normal class and trims the name', () => {
    expect(validateClassForm('  Class 6 - Morning Shift ', ' 120 ')).toEqual({
      ok: true,
      values: { name: 'Class 6 - Morning Shift', totalSeats: 120 },
    })
  })

  it.each([
    ['empty name', '', '10', 'name'],
    ['blank name', '   ', '10', 'name'],
    ['name of 201 characters', 'x'.repeat(201), '10', 'name'],
    ['name with a control character', 'Class\u0000 6', '10', 'name'],
    ['missing seats', 'A', '', 'totalSeats'],
    ['zero seats', 'A', '0', 'totalSeats'],
    ['fractional seats', 'A', '2.5', 'totalSeats'],
    ['negative seats', 'A', '-3', 'totalSeats'],
    ['non-ASCII digits', 'A', '１２', 'totalSeats'],
    ['more seats than a 32-bit integer', 'A', String(MAX_TOTAL_SEATS + 1), 'totalSeats'],
  ] as const)('rejects %s with a message on the right field', (_label, name, seats, field) => {
    const result = validateClassForm(name, seats)
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.errors[field]).toEqual(expect.any(String))
  })

  it('accepts exactly 200 characters and the largest allowed seat count', () => {
    expect(validateClassForm('x'.repeat(200), String(MAX_TOTAL_SEATS)).ok).toBe(true)
  })
})
