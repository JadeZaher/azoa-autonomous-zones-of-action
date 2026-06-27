'use client'

import { usePathname } from 'next/navigation'
import Link from 'next/link'
import { cn } from '@/lib/utils'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Separator } from '@/components/ui/separator'
import { Sheet, SheetContent, SheetTrigger, SheetTitle } from '@/components/ui/sheet'
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
  Star,
  KeyRound,
  Settings,
  FlaskConical,
  Coins,
  Menu,
} from 'lucide-react'
import { useState } from 'react'

const navGroups = [
  {
    label: null,
    items: [
      { href: '/overview', label: 'Overview', icon: LayoutDashboard },
    ],
  },
  {
    label: 'Identity',
    items: [
      { href: '/avatars', label: 'Avatars', icon: Users },
      { href: '/holons', label: 'Holons', icon: Box },
    ],
  },
  {
    label: 'Assets',
    items: [
      { href: '/wallets', label: 'Wallets', icon: Wallet },
      { href: '/nfts', label: 'NFTs', icon: Image },
      { href: '/fungible-mint', label: 'Fungible Mint', icon: Coins },
      { href: '/avatar-nfts', label: 'Avatar NFTs', icon: UserCircle },
    ],
  },
  {
    label: 'DeFi',
    items: [
      { href: '/blockchain', label: 'Blockchain', icon: Link2 },
      { href: '/swap', label: 'Swap', icon: ArrowLeftRight },
      { href: '/bridge', label: 'Bridge', icon: Globe },
    ],
  },
  {
    label: 'Tools',
    items: [
      { href: '/search', label: 'Search', icon: Search },
      { href: '/quests', label: 'Quests', icon: Star },
      { href: '/star-odk', label: 'Star ODK', icon: Star },
      { href: '/api-keys', label: 'API Keys', icon: KeyRound },
    ],
  },
  {
    label: null,
    items: [
      { href: '/settings', label: 'Settings', icon: Settings },
      { href: '/tests', label: 'Tests', icon: FlaskConical },
    ],
  },
]

function NavLinks({ onNavigate }: { onNavigate?: () => void }) {
  const pathname = usePathname()

  return (
    <ScrollArea className="h-full">
      <div className="space-y-1 px-3 py-2">
        {navGroups.map((group, gi) => (
          <div key={gi}>
            {group.label && (
              <p className="mb-1 mt-4 px-2 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground/60 first:mt-0">
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
            {gi < navGroups.length - 1 && <Separator className="mt-3" />}
          </div>
        ))}
      </div>
    </ScrollArea>
  )
}

function Logo() {
  return (
    <div className="flex h-12 items-center gap-2.5 px-4">
      <div className="flex h-7 w-7 items-center justify-center rounded-md bg-primary">
        <span className="text-xs font-bold text-primary-foreground">O</span>
      </div>
      <span className="text-sm font-semibold tracking-tight">AZOA</span>
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
      <SheetTrigger className="inline-flex items-center justify-center rounded-md p-2 hover:bg-accent md:hidden">
        <Menu className="h-5 w-5" />
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
