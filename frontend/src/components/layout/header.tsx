'use client'

import { useOasis } from '@/lib/oasis-context'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { LogOut } from 'lucide-react'
import { MobileSidebar } from './sidebar'
import { NetworkSwitcher } from '@/components/network-switcher'
import { DebugSwitcher } from '@/components/debug-switcher'

interface HeaderProps {}

export function Header() {
  const { user, logout, defaultWallet } = useOasis()

  const initials = user?.username
    ? user.username.slice(0, 2).toUpperCase()
    : '??'

  return (
    <header className="flex h-12 items-center gap-3 border-b border-border bg-card/50 px-4 backdrop-blur-sm">
      <MobileSidebar />

      <div className="flex-1" />

      <DebugSwitcher />

      <NetworkSwitcher />

      <DropdownMenu>
        <DropdownMenuTrigger className="flex items-center gap-2 rounded-md px-2 py-1 text-sm hover:bg-accent transition-colors">
          <Avatar size="sm">
            <AvatarFallback className="text-[10px]">{initials}</AvatarFallback>
          </Avatar>
          <span className="hidden sm:inline text-sm">{user?.username ?? 'User'}</span>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-56">
          <DropdownMenuLabel className="font-normal">
            <div className="flex flex-col gap-1">
              <p className="text-sm font-medium">{user?.username ?? 'User'}</p>
              <p className="text-xs text-muted-foreground">{user?.email ?? ''}</p>
              {defaultWallet && (
                <p className="font-mono text-[11px] text-muted-foreground">
                  {defaultWallet.address.slice(0, 6)}…{defaultWallet.address.slice(-4)}
                </p>
              )}
            </div>
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem onClick={() => logout()}>
            <LogOut className="mr-2 h-4 w-4" />
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </header>
  )
}
