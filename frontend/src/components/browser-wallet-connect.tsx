'use client'

import { useState, useCallback, useEffect } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter,
} from '@/components/ui/dialog'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { useAzoa } from '@/lib/azoa-context'

interface BrowserWalletConnectDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  defaultChain?: string
  /** Restrict chain selection to only these chains. If omitted, all chains are available. */
  allowedChains?: string[]
}

const CHAINS = [
  { value: 'Algorand', label: 'Algorand' },
  { value: 'Solana', label: 'Solana' },
  { value: 'Ethereum', label: 'Ethereum (MetaMask, etc.)' },
]

export function BrowserWalletConnectDialog({ open, onOpenChange, defaultChain, allowedChains }: BrowserWalletConnectDialogProps) {
  const { connectExternalWallet, connectBrowserWallet, browserWalletAvailable } = useAzoa()

  const visibleChains = allowedChains
    ? CHAINS.filter(c => allowedChains.includes(c.value))
    : CHAINS

  const [chainType, setChainType] = useState(defaultChain ?? visibleChains[0]?.value ?? 'Algorand')
  const [label, setLabel] = useState('')
  const [isDefault, setIsDefault] = useState(false)
  const [loading, setLoading] = useState(false)
  const [manualAddress, setManualAddress] = useState('')
  const [mode, setMode] = useState<'auto' | 'manual'>('auto')

  // Sync chainType when allowedChains/defaultChain changes or dialog reopens
  useEffect(() => {
    if (!open) return
    const initial = defaultChain ?? visibleChains[0]?.value ?? 'Algorand'
    const isValid = visibleChains.some(c => c.value === chainType)
    if (!isValid && visibleChains.length > 0) {
      setChainType(visibleChains[0].value)
    } else {
      setChainType(initial)
    }
    setMode('auto')
    setManualAddress('')
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, defaultChain, allowedChains])

  const walletAvailable = browserWalletAvailable(chainType)

  const handleAutoConnect = useCallback(async () => {
    setLoading(true)
    const connResult = await connectBrowserWallet(chainType)
    setLoading(false)

    if (!connResult.success || !connResult.address) {
      toast.error(connResult.error ?? 'Failed to connect wallet')
      return
    }

    // Save to backend
    setLoading(true)
    const saveResult = await connectExternalWallet({
      chainType,
      address: connResult.address,
      label: label.trim() || undefined,
      isDefault,
    })
    setLoading(false)

    if (saveResult.success) {
      toast.success(`${chainType} wallet connected: ${connResult.address.slice(0, 6)}…${connResult.address.slice(-4)}`)
      onOpenChange(false)
      setLabel('')
      setIsDefault(false)
      setManualAddress('')
    } else {
      toast.error(saveResult.error ?? 'Failed to save wallet')
    }
  }, [chainType, label, isDefault, connectBrowserWallet, connectExternalWallet, onOpenChange])

  const handleManualConnect = useCallback(async () => {
    if (!manualAddress.trim()) {
      toast.error('Please enter a wallet address')
      return
    }
    setLoading(true)
    const saveResult = await connectExternalWallet({
      chainType,
      address: manualAddress.trim(),
      label: label.trim() || undefined,
      isDefault,
    })
    setLoading(false)

    if (saveResult.success) {
      toast.success(`${chainType} wallet connected`)
      onOpenChange(false)
      setLabel('')
      setIsDefault(false)
      setManualAddress('')
    } else {
      toast.error(saveResult.error ?? 'Failed to save wallet')
    }
  }, [chainType, manualAddress, label, isDefault, connectExternalWallet, onOpenChange])

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Connect External Wallet</DialogTitle>
        </DialogHeader>

        <div className="space-y-4">
          <div className="space-y-1.5">
            <Label>Chain</Label>
            <Select value={chainType} onValueChange={v => { if (v) { setChainType(v); setMode('auto') } }}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                {visibleChains.map(c => <SelectItem key={c.value} value={c.value}>{c.label}</SelectItem>)}
              </SelectContent>
            </Select>
          </div>

          {/* Auto-connect via browser wallet */}
          {walletAvailable && mode === 'auto' && (
            <div className="rounded-lg border border-green-200 bg-green-50 dark:border-green-900 dark:bg-green-950 p-3 text-sm">
              <p className="font-medium text-green-800 dark:text-green-200">🦊 Browser wallet detected!</p>
              <p className="text-green-600 dark:text-green-400 text-xs mt-1">
                Click below to connect via your installed browser wallet.
              </p>
              <Button
                className="mt-2 w-full"
                onClick={handleAutoConnect}
                disabled={loading}
              >
                {loading ? 'Connecting...' : `Connect ${chainType} Wallet`}
              </Button>
            </div>
          )}

          {!walletAvailable && mode === 'auto' && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 text-sm">
              <p className="text-amber-800 dark:text-amber-200">
                No browser wallet detected for {chainType}.
              </p>
              <p className="text-amber-600 dark:text-amber-400 text-xs mt-1">
                Install MetaMask (Ethereum), Phantom (Solana), or Pera Wallet (Algorand).
              </p>
              <Button variant="outline" size="sm" className="mt-2" onClick={() => setMode('manual')}>
                Enter address manually
              </Button>
            </div>
          )}

          {/* Manual address entry */}
          {mode === 'manual' && (
            <div className="space-y-1.5">
              <Label>Wallet Address *</Label>
              <Input
                placeholder={`Enter ${chainType} address...`}
                value={manualAddress}
                onChange={e => setManualAddress(e.target.value)}
              />
              <Button variant="outline" size="sm" className="mt-1" onClick={() => setMode('auto')}>
                Try auto-connect
              </Button>
            </div>
          )}

          <div className="space-y-1.5">
            <Label>Label (optional)</Label>
            <Input placeholder="e.g., My MetaMask" value={label} onChange={e => setLabel(e.target.value)} />
          </div>

          <div className="flex items-center gap-2">
            <Checkbox id="ext-isDefault" checked={isDefault} onCheckedChange={c => setIsDefault(c === true)} />
            <Label htmlFor="ext-isDefault">Set as default wallet</Label>
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          {mode === 'manual' && (
            <Button onClick={handleManualConnect} disabled={loading || !manualAddress.trim()}>
              {loading ? 'Saving...' : 'Connect'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}