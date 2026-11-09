import { describe, expect, it } from 'vitest'
import { resolveApiBaseUrl } from './config'

describe('resolveApiBaseUrl', () => {
  it.each([undefined, '', '   '])('falls back to the contract address for %j', (value) => {
    expect(resolveApiBaseUrl(value)).toBe('http://localhost:8080')
  })

  it('uses the configured address and removes trailing slashes and spaces', () => {
    expect(resolveApiBaseUrl(' http://api.example:9000/// ')).toBe('http://api.example:9000')
  })
})
