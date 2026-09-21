import { useEffect, useState } from 'react'

/** value가 delayMs 동안 바뀌지 않았을 때만 따라오는 값. */
export function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delayMs)
    return () => window.clearTimeout(timer)
  }, [value, delayMs])
  return debounced
}

export const formatDateTime = (iso: string): string => {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('ko-KR', { dateStyle: 'medium', timeStyle: 'short' })
}

export const formatBytes = (bytes: number): string =>
  bytes < 1024 ? `${bytes}B` : bytes < 1_048_576 ? `${(bytes / 1024).toFixed(1)}KB` : `${(bytes / 1_048_576).toFixed(1)}MB`
