'use client'

import { useState } from 'react'
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
import { useOasis } from '@/lib/oasis-context'
import { Key, AlertTriangle } from 'lucide-react'

interface PlatformWalletGenerateDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

const CHAINS = [
  { value: 'Algorand', label: 'Algorand', icon: '🔵' },
  { value: 'Solana', label: 'Solana', icon: '🟣' },
  { value: 'Ethereum', label: 'Ethereum', icon: '🔷' },
]

export function PlatformWalletGenerateDialog({ open, onOpenChange }: PlatformWalletGenerateDialogProps) {
  const { generateWallet } = useOasis()
  const [chainType, setChainType] = useState('Algorand')
  const [label, setLabel] = useState('')
  const [isDefault, setIsDefault] = useState(false)
  const [loading, setLoading] = useState(false)
  const [acknowledged, setAcknowledged] = useState(false)

  const handleGenerate = async () => {
    if (!acknowledged) {
      toast.error('Please acknowledge the warning above')
      return
    }
    setLoading(true)
    const result = await generateWallet({
      chainType,
      label: label.trim() || undefined,
      isDefault,
    })
    setLoading(false)

    if (result.success) {
      toast.success(`${chainType} wallet created on platform!`)
      onOpenChange(false)
      setLabel('')
      setIsDefault(false)
      setAcknowledged(false)
    } else {
      toast.error(result.error ?? 'Failed to generate wallet')
    }
  }

  const selectedChain = CHAINS.find(c => c.value === chainType)

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Key className="h-5 w-5" />
            Create Platform Wallet
          </DialogTitle>
        </DialogHeader>

        <div className="space-y-4">
          {/* Warning */}
          <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 flex gap-2">
            <AlertTriangle className="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5" />
            <div className="text-xs text-amber-800 dark:text-amber-200 space-y-1">
              <p className="font-medium">Important</p>
              <p>This creates a wallet managed by the OASIS platform. Your private key is encrypted and stored securely.</p>
              <p>You can export your private key / seed phrase later from the wallet details. <strong>Keep these safe — they cannot be recovered if lost.</strong></p>
            </div>
          </div>

          <div className="space-y-1.5">
            <Label>Chain</Label>
            <Select value={chainType} onValueChange={v => { if (v) setChainType(v) }}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                {CHAINS.map(c => (
                  <SelectItem key={c.value} value={c.value}>
                    <span>{c.icon} {c.label}</span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-1.5">
            <Label>Label (optional)</Label>
            <Input
              placeholder={`e.g., My ${selectedChain?.label ?? ''} Wallet`}
              value={label}
              onChange={e => setLabel(e.target.value)}
            />
          </div>

          <div className="flex items-center gap-2">
            <Checkbox id="plt-isDefault" checked={isDefault} onCheckedChange={c => setIsDefault(c === true)} />
            <Label htmlFor="plt-isDefault">Set as default wallet</Label>
          </div>

          <div className="flex items-start gap-2 border rounded-lg p-3">
            <Checkbox
              id="plt-ack"
              checked={acknowledged}
              onCheckedChange={c => setAcknowledged(c === true)}
            />
            <Label htmlFor="plt-ack" className="text-xs text-muted-foreground leading-relaxed">
              I understand that I am responsible for backing up my wallet keys.
              The platform stores them encrypted but I should export and save my seed phrase securely.
            </Label>
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={handleGenerate} disabled={loading || !acknowledged}>
            {loading ? 'Generating...' : `Generate ${selectedChain?.icon ?? ''} ${chainType} Wallet`}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}