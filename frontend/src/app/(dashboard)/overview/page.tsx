'use client'

import Link from 'next/link'
import { useState } from 'react'
import { Check, Copy, ArrowRight, ShieldCheck, ReceiptText, Workflow } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button, buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useChainInfo } from '@/lib/azoa-hooks'
import { ChainBadge } from '@/components/shared/chain-badge'
import { LoadingSkeleton } from '@/components/shared/loading-skeleton'

function readDappRole(user: unknown): string {
  if (user && typeof user === 'object' && 'dappRole' in user) {
    const role = (user as { dappRole?: unknown }).dappRole
    if (typeof role === 'string' && role.trim().length > 0) return role
  }
  return 'dapp:user'
}

const QUICKSTART_STEPS = [
  {
    step: '1',
    title: 'Verify identity & business',
    description: 'Complete Identomat person or KYB verification to unlock value movement and verified receipts.',
    icon: ShieldCheck,
    primaryHref: '/verify-identity',
    primaryLabel: 'Verify identity',
    secondaryHref: '/business-verification',
    secondaryLabel: 'Business verification',
  },
  {
    step: '2',
    title: 'Send invoices & ride-along links',
    description: 'Issue a signed /ride link, invoice, or payment request that counterparties can verify and settle in one link.',
    icon: ReceiptText,
    primaryHref: '/documents',
    primaryLabel: 'Documents & invoices',
    secondaryHref: '/funds',
    secondaryLabel: 'Add funds',
  },
  {
    step: '3',
    title: 'Build & run workflows',
    description: 'Launch from a 1-click starter blueprint or AI brief with Decision Model plus Critic & Auditor checkpoints.',
    icon: Workflow,
    primaryHref: '/actions',
    primaryLabel: 'Open workflows',
    secondaryHref: '/wallets',
    secondaryLabel: 'Wallets',
  },
] as const

function ChainInfoCard({ chain }: { chain: string }) {
  const { info, loading, error } = useChainInfo(chain)

  if (loading) return <LoadingSkeleton />

  return (
    <Card role="region" aria-label={`${chain} network status`}>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-sm">
          <ChainBadge chain={chain} />
          <span className="capitalize">{chain}</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        {error ? (
          <p role="status" className="text-muted-foreground">
            Network details could not be loaded. Try again later.
          </p>
        ) : info ? (
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
  const { userId, isAuthenticated, user } = useAzoaAuth()
  const [copiedId, setCopiedId] = useState(false)
  const dappRole = readDappRole(user)

  const copyUserId = async () => {
    if (!userId || typeof navigator === 'undefined' || !navigator.clipboard) return
    await navigator.clipboard.writeText(userId)
    setCopiedId(true)
    setTimeout(() => setCopiedId(false), 1800)
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Overview</h1>
        <p className="text-sm text-muted-foreground">Start a core workflow, inspect account access, and check network health</p>
      </div>

      <section aria-label="First-mile quickstart" className="space-y-3">
        <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
          First-Mile Quickstart
        </p>
        <div className="grid gap-4 md:grid-cols-3">
          {QUICKSTART_STEPS.map((item) => {
            const Icon = item.icon
            return (
              <Card key={item.step} className="flex flex-col justify-between">
                <CardHeader className="pb-2">
                  <CardTitle className="flex items-center gap-2 text-sm">
                    <Icon className="h-4 w-4 text-primary" aria-hidden="true" />
                    <span>{item.step}. {item.title}</span>
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4 text-sm">
                  <p className="text-xs text-muted-foreground">{item.description}</p>
                  <div className="flex flex-wrap items-center gap-2">
                    <Link href={item.primaryHref} className={buttonVariants({ size: 'sm' })}>
                      {item.primaryLabel}
                      <ArrowRight className="ml-1.5 h-3.5 w-3.5" aria-hidden="true" />
                    </Link>
                    <Link href={item.secondaryHref} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
                      {item.secondaryLabel}
                    </Link>
                  </div>
                </CardContent>
              </Card>
            )
          })}
        </div>
      </section>

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
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-muted-foreground">User ID</span>
            <div className="flex items-center gap-2">
              <span className="font-mono text-xs">{userId ?? '—'}</span>
              {userId && (
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  className="h-7 px-2 text-xs"
                  aria-label="Copy user ID"
                  onClick={() => void copyUserId()}
                >
                  {copiedId ? <Check className="mr-1 h-3.5 w-3.5 text-primary" aria-hidden="true" /> : <Copy className="mr-1 h-3.5 w-3.5" aria-hidden="true" />}
                  {copiedId ? 'Copied' : 'Copy'}
                </Button>
              )}
            </div>
          </div>
          <Separator />
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-muted-foreground">DApp Role</span>
            <div className="flex items-center gap-2">
              <Badge variant="outline" className="font-mono text-[11px]">{dappRole}</Badge>
              {dappRole === 'dapp:user' && (
                <Link href="/admin/roles" className="text-xs underline underline-offset-4">
                  Role assignment
                </Link>
              )}
            </div>
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
