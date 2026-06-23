'use client'

import { useEffect } from 'react'
import { useRouter } from 'next/navigation'
import { useAzoaAuth } from '@/lib/azoa-auth'

export default function Home() {
  const { isAuthenticated, loading } = useAzoaAuth()
  const router = useRouter()

  useEffect(() => {
    if (!loading) {
      router.replace(isAuthenticated ? '/overview' : '/login')
    }
  }, [loading, isAuthenticated, router])

  return (
    <div className="flex h-screen items-center justify-center bg-background">
      <div className="h-5 w-5 animate-spin rounded-full border-2 border-primary border-t-transparent" />
    </div>
  )
}
