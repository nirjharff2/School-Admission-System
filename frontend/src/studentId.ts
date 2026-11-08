import { useSyncExternalStore } from 'react'

const STORAGE_KEY = 'admission.studentId'

/** A birth registration number is exactly 17 ASCII digits (the backend rejects anything else with 400). */
export function isValidStudentId(value: string): boolean {
  return /^[0-9]{17}$/.test(value)
}

function readStored(): string {
  try {
    return localStorage.getItem(STORAGE_KEY) ?? ''
  } catch {
    return '' // storage blocked (private mode, policy): the box still works for this tab
  }
}

let current = readStored()
const listeners = new Set<() => void>()

function notify() {
  for (const listener of listeners) listener()
}

/** The current student id as typed (trimmed), valid or not. Persisted so it survives refresh and browser restarts. */
export function getStudentId(): string {
  return current
}

export function setStudentId(value: string) {
  const next = value.trim()
  if (next === current) return
  current = next
  try {
    if (next) localStorage.setItem(STORAGE_KEY, next)
    else localStorage.removeItem(STORAGE_KEY)
  } catch {
    // not persisted; keep the in-memory value
  }
  notify()
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  const onStorage = (event: StorageEvent) => {
    // another tab changed the id (or cleared storage): follow it
    if (event.key === STORAGE_KEY || event.key === null) {
      current = readStored()
      listener()
    }
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

export function useStudentId() {
  const studentId = useSyncExternalStore(subscribe, getStudentId)
  return { studentId, valid: isValidStudentId(studentId), setStudentId }
}
