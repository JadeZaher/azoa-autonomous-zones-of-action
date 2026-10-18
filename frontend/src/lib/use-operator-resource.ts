'use client'

import { useCallback, useEffect, useState } from 'react'
import { OperatorRequestError, operatorRequest } from '@/lib/operator-client'

/** Reads one allowlisted operator path through the BFF; `path === null` reads nothing. */
export function useOperatorResource<T>(path: string | null, fallbackError: string) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorCode, setErrorCode] = useState<string | null>(null)
  const [loading, setLoading] = useState(path !== null)

  /** Re-reads the path; resolves to the fresh value, or null after recording the error. */
  const reload = useCallback(async (): Promise<T | null> => {
    if (path === null) return null
    setLoading(true)
    setError(null)
    setErrorCode(null)
    try {
      const value = await operatorRequest<T>(path)
      setData(value)
      return value
    } catch (cause) {
      setData(null)
      setError(cause instanceof Error ? cause.message : fallbackError)
      setErrorCode(cause instanceof OperatorRequestError ? cause.code ?? null : null)
      return null
    } finally {
      setLoading(false)
    }
  }, [path, fallbackError])

  useEffect(() => { void reload() }, [reload])
  return { data, error, errorCode, loading, reload }
}

/** Runs one operator mutation; returns the result, or null after recording the refusal. Never reports success itself. */
export function useOperatorMutation() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = useCallback(async <T,>(path: string, method: 'POST' | 'PUT', body: unknown): Promise<T | null> => {
    setBusy(true)
    setError(null)
    try {
      return await operatorRequest<T>(path, { method, body })
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The node did not accept this action.')
      return null
    } finally {
      setBusy(false)
    }
  }, [])

  return { busy, error, setError, run }
}
