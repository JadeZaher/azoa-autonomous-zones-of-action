'use client'

import { Badge } from '@/components/ui/badge'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { Globe, Key } from 'lucide-react'

interface WalletTypeBadgeProps {
  walletType: 'External' | 'Platform'
  className?: string
}

export function WalletTypeBadge({ walletType, className }: WalletTypeBadgeProps) {
  if (walletType === 'External') {
    return (
      <Tooltip>
        <TooltipTrigger>
          <Badge variant="secondary" className={`gap-1 text-[10px] cursor-default ${className ?? ''}`}>
            <Globe className="h-3 w-3" />
            External
          </Badge>
        </TooltipTrigger>
        <TooltipContent>
          <p className="text-xs">Connected via browser wallet (MetaMask, Phantom, Pera, etc.)</p>
        </TooltipContent>
      </Tooltip>
    )
  }

  return (
    <Tooltip>
      <TooltipTrigger>
        <Badge variant="default" className={`gap-1 text-[10px] bg-amber-600 hover:bg-amber-700 cursor-default ${className ?? ''}`}>
          <Key className="h-3 w-3" />
          Platform
        </Badge>
      </TooltipTrigger>
      <TooltipContent>
        <p className="text-xs">Generated and managed by AZOA platform. Keys stored encrypted.</p>
      </TooltipContent>
    </Tooltip>
  )
}