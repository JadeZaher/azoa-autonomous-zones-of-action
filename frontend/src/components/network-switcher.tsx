'use client'

import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { useNetwork } from '@/lib/network-context'
import { NETWORK_META } from '@/lib/networks'

/**
 * Global network switcher. Changing the value persists the choice and reloads
 * so every layer re-initialises against the selected network.
 */
export function NetworkSwitcher({ size = 'sm' }: { size?: 'sm' | 'default' }) {
  const { network, networks, setNetwork } = useNetwork()

  return (
    <Select
      value={network}
      onValueChange={(v) => { if (v) setNetwork(v as typeof network) }}
    >
      <SelectTrigger size={size} aria-label="Select network" className="gap-2">
        <span
          className={`inline-block size-2 rounded-full ${NETWORK_META[network].dotClass}`}
          aria-hidden
        />
        <SelectValue />
      </SelectTrigger>
      <SelectContent align="end">
        {networks.map((env) => {
          const m = NETWORK_META[env]
          return (
            <SelectItem key={env} value={env}>
              <span>
                <span className={`inline-block size-2 rounded-full ${m.dotClass}`} aria-hidden />
                {m.label}
              </span>
            </SelectItem>
          )
        })}
      </SelectContent>
    </Select>
  )
}
