'use client'

import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'

export function LoadingSkeleton() {
  return (
    <Card>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-5 w-36" />
        <Skeleton className="h-4 w-56" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-3/4" />
      </CardContent>
    </Card>
  )
}
