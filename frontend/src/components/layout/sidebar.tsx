'use client'

import { usePathname } from 'next/navigation'
import Link from 'next/link'
import { cn } from '@/lib/utils'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Separator } from '@/components/ui/separator'
import { Sheet, SheetContent, SheetTrigger, SheetTitle } from '@/components/ui/sheet'
import type { LucideIcon } from 'lucide-react'
import {
  LayoutDashboard,
  Users,
  Box,
  Wallet,
  Image,
  UserCircle,
  Link2,
  ArrowLeftRight,
  Globe,
  Search,
  Package,
  Workflow,
  KeyRound,
  Settings,
  FlaskConical,
  Coins,
  Menu,
  Database,
  ShieldAlert,
  GitMerge,
  BadgeCheck,
  BarChart3,
  Clock,
  Undo2,
  Banknote,
  Scale,
  ShieldCheck,
  PiggyBank,
  HandCoins,
  Landmark,
} from 'lucide-react'
import { useState } from 'react'

/** Navigation audience; see AGENTS.md §Audience. */
type NavAudience = 'all' | 'operator'

interface NavItem {
  href: string
  label: string
  icon: LucideIcon
  /** Overrides the owning group's `audience` for just this item. */
  audience?: NavAudience
}

interface NavGroup {
  label: string | null
  audience: NavAudience
  items: NavItem[]
}

/** Explicit console-instance opt-in, defaulting to hidden operator navigation. */
function isOperatorConsole(): boolean {
  return process.env.NEXT_PUBLIC_AZOA_OPERATOR_CONSOLE === 'true'
}

/** Whether the current console instance may see a nav entry with this resolved audience. */
function isNavAudienceVisible(audience: NavAudience): boolean {
  return audience === 'all' || isOperatorConsole()
}

const navGroups: NavGroup[] = [
  {
    label: null,
    audience: 'all',
    items: [
      { href: '/overview', label: 'Overview', icon: LayoutDashboard },
    ],
  },
  {
    label: 'Identity',
    audience: 'all',
    items: [
      { href: '/users', label: 'Users', icon: Users },
      { href: '/holons', label: 'Holons', icon: Box },
      { href: '/kyc', label: 'KYC Provider', icon: BadgeCheck },
      { href: '/business-verification', label: 'Business verification', icon: BadgeCheck },
    ],
  },
  {
    label: 'Money',
    audience: 'all',
    items: [
      { href: '/verify-identity', label: 'Verify identity', icon: ShieldCheck },
      { href: '/funds', label: 'Add funds', icon: PiggyBank },
      { href: '/cash-out', label: 'Cash out', icon: HandCoins },
      { href: '/payout-balance', label: 'Payout balance', icon: Landmark },
    ],
  },
  {
    label: 'Assets',
    audience: 'all',
    items: [
      { href: '/wallets', label: 'Wallets', icon: Wallet },
      { href: '/nfts', label: 'NFTs', icon: Image },
      { href: '/fungible-mint', label: 'Fungible Mint', icon: Coins },
      { href: '/holds', label: 'Hold recovery', icon: Clock },
      { href: '/user-nfts', label: 'User NFTs', icon: UserCircle },
    ],
  },
  // OI-84: a "DeFi" group is the platform announcing itself as a blockchain company on
  // every page load. Operator-only until a real audience picker replaces this raw label.
  {
    label: 'DeFi',
    audience: 'operator',
    items: [
      { href: '/blockchain', label: 'Blockchain', icon: Link2 },
      { href: '/swap', label: 'Swap', icon: ArrowLeftRight },
      { href: '/bridge', label: 'Bridge', icon: Globe },
    ],
  },
  {
    label: 'Tools',
    audience: 'all',
    items: [
      { href: '/search', label: 'Search', icon: Search },
      // OI-84: raw holon queries are an internals surface, not a builder-facing tool.
      { href: '/holon-query', label: 'Holon Query', icon: Database, audience: 'operator' },
      { href: '/actions', label: 'Actions', icon: Workflow },
      { href: '/action-graph-bundle', label: 'Action Graph Bundles', icon: Package },
      { href: '/api-keys', label: 'API Keys', icon: KeyRound },
      { href: '/analytics', label: 'Analytics', icon: BarChart3 },
    ],
  },
  {
    label: 'Admin',
    audience: 'all',
    items: [
      { href: '/admin/key-rotation', label: 'Key Rotation', icon: ShieldAlert },
      // Operator-policy boards live in the operator console (QA M1); these open its sign-in.
      { href: '/operator/sagas', label: 'Saga Console', icon: GitMerge },
      { href: '/operator/parked-runs', label: 'Waiting on Settlement', icon: Clock },
      { href: '/operator/reversals', label: 'Returned Payments', icon: Undo2 },
      { href: '/operator/payouts', label: 'Cash-outs', icon: Banknote },
      { href: '/node-transparency', label: 'Node Economic Rules', icon: Scale },
    ],
  },
  {
    label: null,
    audience: 'all',
    items: [
      { href: '/settings', label: 'Settings', icon: Settings },
      { href: '/tests', label: 'Tests', icon: FlaskConical },
    ],
  },
]

function NavLinks({ onNavigate }: { onNavigate?: () => void }) {
  const pathname = usePathname()

  // Resolve visible items per group first so a group hidden in full (every item's
  // resolved audience denied) renders neither its label nor a trailing separator.
  const visibleGroups = navGroups
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => isNavAudienceVisible(item.audience ?? group.audience)),
    }))
    .filter((group) => group.items.length > 0)

  return (
    <ScrollArea className="h-full">
      <div className="space-y-1 px-3 py-2">
        {visibleGroups.map((group, gi) => (
          <div key={gi}>
            {group.label && (
              <p className="mb-1 mt-4 px-2 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground first:mt-0">
                {group.label}
              </p>
            )}
            {group.items.map((item) => {
              const Icon = item.icon
              const isActive = pathname === item.href
              return (
                <Link
                  key={item.href}
                  href={item.href}
                  onClick={onNavigate}
                  className={cn(
                    'flex items-center gap-3 rounded-md px-2 py-1.5 text-sm transition-colors',
                    isActive
                      ? 'bg-primary/10 font-medium text-primary'
                      : 'text-muted-foreground hover:bg-accent hover:text-foreground'
                  )}
                >
                  <Icon className="h-4 w-4 shrink-0" />
                  {item.label}
                </Link>
              )
            })}
            {gi < visibleGroups.length - 1 && <Separator className="mt-3" />}
          </div>
        ))}
      </div>
    </ScrollArea>
  )
}

function Logo() {
  return (
    <div className="flex h-12 items-center gap-2.5 px-4">
      <div className="flex h-7 w-7 items-center justify-center rounded-sm bg-primary">
        <span className="text-xs font-bold text-primary-foreground">A</span>
      </div>
      <span className="text-sm font-bold tracking-[0.08em]">AZOA</span>
    </div>
  )
}

export function Sidebar() {
  return (
    <aside className="hidden w-56 shrink-0 flex-col border-r border-border bg-sidebar md:flex">
      <Logo />
      <Separator />
      <NavLinks />
    </aside>
  )
}

export function MobileSidebar() {
  const [open, setOpen] = useState(false)

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger aria-label="Open navigation" className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-md p-2 hover:bg-accent md:hidden">
        <Menu className="h-5 w-5" aria-hidden="true" />
      </SheetTrigger>
      <SheetContent side="left" className="w-56 p-0">
        <SheetTitle className="sr-only">Navigation</SheetTitle>
        <Logo />
        <Separator />
        <NavLinks onNavigate={() => setOpen(false)} />
      </SheetContent>
    </Sheet>
  )
}
