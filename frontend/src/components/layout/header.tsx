'use client'

import { useAzoa } from '@/lib/azoa-context'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { LogOut, ShieldOff } from 'lucide-react'
import { MobileSidebar } from './sidebar'
import { NetworkSwitcher } from '@/components/network-switcher'
import { DebugSwitcher } from '@/components/debug-switcher'

interface HeaderProps {}

export function Header() {
  const { user, logout, logoutEverywhere, defaultWallet } = useAzoa()

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
          {/*
            The label MUST stay inside DropdownMenuGroup. Base UI's `Menu.GroupLabel`
            reads `MenuGroupRootContext` and THROWS when it is missing — in production
            builds too — which takes the whole React tree down the first time a user
            opens this menu.
          */}
          <DropdownMenuGroup>
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
          </DropdownMenuGroup>
          <DropdownMenuSeparator />
          <DropdownMenuItem onClick={() => logout()}>
            <LogOut className="mr-2 h-4 w-4" />
            Sign out
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => logoutEverywhere()} variant="destructive">
            <ShieldOff className="mr-2 h-4 w-4" />
            Log out of all devices
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </header>
  )
}
