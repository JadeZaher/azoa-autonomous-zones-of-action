'use client'

import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'

interface ChainBadgeProps {
  chain: string
  className?: string
}

const chainStyles: Record<string, string> = {
  algorand: 'border-blue-500/30 bg-blue-500/10 text-blue-400 dark:border-blue-400/30 dark:text-blue-300',
  solana: 'border-purple-500/30 bg-purple-500/10 text-purple-400 dark:border-purple-400/30 dark:text-purple-300',
}

export function ChainBadge({ chain, className }: ChainBadgeProps) {
  const normalized = chain.toLowerCase()
  const style = chainStyles[normalized] ?? 'border-border bg-muted text-muted-foreground'

  return (
    <Badge variant="outline" className={cn('text-[11px] font-medium', style, className)}>
      {chain}
    </Badge>
  )
}
