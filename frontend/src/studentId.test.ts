import { describe, expect, it, vi } from 'vitest'
import { getStudentId, isValidStudentId, setStudentId } from './studentId'

describe('isValidStudentId', () => {
  it.each(['20152691234567890', '00000000000000000'])('accepts exactly 17 ASCII digits: %s', (id) => {
    expect(isValidStudentId(id)).toBe(true)
  })

  it.each([
    ['too short', '2015269123456789'],
    ['too long', '201526912345678901'],
    ['letters', '2015269123456789a'],
    ['spaces inside', '2015269 1234567890'],
    ['full-width digits', '２０１５２６９１２３４５６７８９０'],
    ['empty', ''],
  ])('rejects %s', (_label, id) => {
    expect(isValidStudentId(id)).toBe(false)
  })
})

describe('the saved student id', () => {
  it('is written to localStorage, trimmed, and removed when cleared', () => {
    setStudentId('  20152691234567890 ')
    expect(getStudentId()).toBe('20152691234567890')
    expect(localStorage.getItem('admission.studentId')).toBe('20152691234567890')

    setStudentId('')
    expect(localStorage.getItem('admission.studentId')).toBeNull()
  })

  it('survives a page reload: a fresh load of the module reads it back', async () => {
    setStudentId('20152691234567890')

    vi.resetModules() // what a reload does: the module state is gone, only localStorage is left
    const reloaded = await import('./studentId')

    expect(reloaded.getStudentId()).toBe('20152691234567890')
  })

  it('keeps a half-typed (invalid) id too, so typing is not lost on refresh', async () => {
    setStudentId('2015')

    vi.resetModules()
    const reloaded = await import('./studentId')

    expect(reloaded.getStudentId()).toBe('2015')
    expect(reloaded.isValidStudentId(reloaded.getStudentId())).toBe(false)
  })
})
