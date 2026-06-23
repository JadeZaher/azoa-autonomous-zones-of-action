'use client'

import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useChainInfo } from '@/lib/azoa-hooks'
import { ChainBadge } from '@/components/shared/chain-badge'
import { LoadingSkeleton } from '@/components/shared/loading-skeleton'
import { ErrorBanner } from '@/components/shared/error-banner'

function ChainInfoCard({ chain }: { chain: string }) {
  const { info, loading, error } = useChainInfo(chain)

  if (loading) return <LoadingSkeleton />
  if (error) return <ErrorBanner message={error} />

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-sm">
          <ChainBadge chain={chain} />
          <span className="capitalize">{chain}</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        {info ? (
          Object.entries(info).map(([key, value]) => (
            <div key={key} className="flex justify-between gap-4">
              <span className="text-muted-foreground">
                {key.replace(/([A-Z])/g, ' $1').trim().toLowerCase()}
              </span>
              <span className="font-mono text-xs text-right break-all max-w-[60%]">
                {String(value)}
              </span>
            </div>
          ))
        ) : (
          <p className="text-muted-foreground">No info available</p>
        )}
      </CardContent>
    </Card>
  )
}

export default function OverviewPage() {
  const { avatarId, isAuthenticated, user } = useAzoaAuth()

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Overview</h1>
        <p className="text-sm text-muted-foreground">System status and network information</p>
      </div>

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Session</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground">Status</span>
            <Badge variant={isAuthenticated ? 'default' : 'destructive'} className="text-[11px]">
              {isAuthenticated ? 'Authenticated' : 'Unauthenticated'}
            </Badge>
          </div>
          <Separator />
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground">Avatar</span>
            <span className="font-mono text-xs">{avatarId ?? '—'}</span>
          </div>
          {user && (
            <>
              <Separator />
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Username</span>
                <span>{user.username}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Email</span>
                <span>{user.email}</span>
              </div>
            </>
          )}
        </CardContent>
      </Card>

      <div>
        <p className="mb-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
          Chain Status
        </p>
        <div className="grid gap-4 md:grid-cols-2">
          <ChainInfoCard chain="algorand" />
          <ChainInfoCard chain="solana" />
        </div>
      </div>
    </div>
  )
}
