import { describe, expect, it } from 'vitest'
import { effectiveStatus, refreshDelayFor } from './holdStatus'

const deadline = Date.parse('2026-11-01T04:10:00Z')
const iso = '2026-11-01T04:10:00Z'

describe('effectiveStatus', () => {
  it('leaves a HELD hold alone before its expiry', () => {
    expect(effectiveStatus('HELD', iso, deadline - 1, deadline - 5000)).toEqual({ status: 'HELD', serverDisagrees: false })
  })

  it('shows a HELD hold as EXPIRED the moment the clock reaches expiresAt, without waiting for the server', () => {
    expect(effectiveStatus('HELD', iso, deadline, deadline - 5000)).toEqual({ status: 'EXPIRED', serverDisagrees: false })
  })

  it('lets the server overrule the clock when a request sent after expiresAt still says HELD (browser clock ahead)', () => {
    expect(effectiveStatus('HELD', iso, deadline + 3000, deadline + 500)).toEqual({ status: 'HELD', serverDisagrees: true })
  })

  it.each(['CONFIRMED', 'CANCELLED', 'EXPIRED'] as const)('never changes a %s hold, however late it is', (status) => {
    expect(effectiveStatus(status, iso, deadline + 60_000, deadline - 5000)).toEqual({ status, serverDisagrees: false })
  })

  it('does not invent an expiry from an unreadable date', () => {
    expect(effectiveStatus('HELD', 'not a date', deadline, 0)).toEqual({ status: 'HELD', serverDisagrees: false })
  })
})

describe('refreshDelayFor', () => {
  const slow = 15_000

  it('refetches slowly while a hold has plenty of time left', () => {
    expect(refreshDelayFor([{ status: 'HELD', expiresAt: iso }], deadline - 120_000, slow, null)).toBe(slow)
  })

  it('refetches just after a hold is due to expire', () => {
    expect(refreshDelayFor([{ status: 'HELD', expiresAt: iso }], deadline - 4_000, slow, null)).toBe(4_500)
  })

  it('polls every couple of seconds while the server has not yet confirmed an expiry', () => {
    expect(refreshDelayFor([{ status: 'HELD', expiresAt: iso }], deadline + 1_000, slow, null)).toBe(2_000)
  })

  it('stops when nothing can change, and keeps the idle pace when asked to', () => {
    const settled = [{ status: 'CONFIRMED' as const, expiresAt: iso }]
    expect(refreshDelayFor(settled, deadline, slow, null)).toBeNull()
    expect(refreshDelayFor(settled, deadline, slow, 15_000)).toBe(15_000)
  })
})
