import { useEffect, useRef } from 'react'

/** Invalidates asynchronous responses when the account, asset or request context changes. */
export function useNftRequestScope(identity: string) {
  const generation = useRef(0)
  useEffect(() => () => { generation.current += 1 }, [identity])
  return generation
}
