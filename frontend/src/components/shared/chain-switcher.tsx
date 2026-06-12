'use client'

import { Button } from '@/components/ui/button'
import { ChainBadge } from '@/components/shared/chain-badge'
import { cn } from '@/lib/utils'

export interface ChainOption {
  value: string
  label: string
}

interface ChainSwitcherProps {
  options: ChainOption[]
  value: string
  onChange: (value: string) => void
  /** Optional className for the wrapper */
  className?: string
}

export function ChainSwitcher({ options, value, onChange, className }: ChainSwitcherProps) {
  return (
    <div className={cn('flex items-center gap-1.5', className)}>
      {options.map((opt) => {
        const isActive = value === opt.value
        return (
          <Button
            key={opt.value}
            type="button"
            size="sm"
            variant={isActive ? 'default' : 'outline'}
            className={cn(
              'gap-1.5 h-7 px-2.5 text-xs transition-all',
              isActive
                ? 'shadow-sm'
                : 'text-muted-foreground hover:text-foreground',
            )}
            onClick={() => onChange(opt.value)}
          >
            <ChainBadge chain={opt.value} />
            <span className="hidden sm:inline">{opt.label}</span>
          </Button>
        )
      })}
    </div>
  )
}
