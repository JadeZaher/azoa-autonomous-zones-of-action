'use client'

import { useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter,
} from '@/components/ui/dialog'
import { useAzoa, type WalletEntry, type WalletExportData } from '@/lib/azoa-context'
import { AlertTriangle, Eye, EyeOff, Copy, Check } from 'lucide-react'

interface WalletExportDialogProps {
  wallet: WalletEntry
  open: boolean
  onOpenChange: (open: boolean) => void
}

export function WalletExportDialog({ wallet, open, onOpenChange }: WalletExportDialogProps) {
  const { exportWallet } = useAzoa()
  const [loading, setLoading] = useState(false)
  const [exportData, setExportData] = useState<WalletExportData | null>(null)
  const [showPrivateKey, setShowPrivateKey] = useState(false)
  const [showSeedPhrase, setShowSeedPhrase] = useState(false)
  const [copiedKey, setCopiedKey] = useState(false)
  const [copiedSeed, setCopiedSeed] = useState(false)

  const handleExport = async () => {
    if (wallet.walletType !== 'Platform') {
      toast.error('Only platform-generated wallets can be exported')
      return
    }
    setLoading(true)
    const result = await exportWallet(wallet.id)
    setLoading(false)

    if (result.success && result.data) {
      setExportData(result.data)
    } else {
      toast.error(result.error ?? 'Failed to export wallet')
    }
  }

  const handleCopy = async (text: string, setCopied: (v: boolean) => void) => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      toast.error('Failed to copy')
    }
  }

  const handleClose = () => {
    onOpenChange(false)
    // Reset after dialog closes
    setTimeout(() => {
      setExportData(null)
      setShowPrivateKey(false)
      setShowSeedPhrase(false)
    }, 200)
  }

  return (
    <Dialog open={open} onOpenChange={handleClose}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>
            {exportData ? `Wallet Details: ${wallet.label ?? wallet.address.slice(0, 8)}…` : 'Export Wallet'}
          </DialogTitle>
        </DialogHeader>

        {!exportData ? (
          <div className="space-y-4">
            <div className="rounded-lg border border-red-200 bg-red-50 dark:border-red-900 dark:bg-red-950 p-3 flex gap-2">
              <AlertTriangle className="h-5 w-5 text-red-600 dark:text-red-400 shrink-0 mt-0.5" />
              <div className="text-xs text-red-800 dark:text-red-200 space-y-1">
                <p className="font-medium">⚠️ Security Warning</p>
                <p>Your private key and seed phrase grant full access to your wallet and funds.</p>
                <p>Only export in a secure environment. Never share with anyone.</p>
                <p><strong>The platform cannot recover these if lost.</strong></p>
              </div>
            </div>

            <div className="space-y-2 text-sm text-muted-foreground">
              <p><strong>Chain:</strong> {wallet.chainType}</p>
              <p><strong>Address:</strong> <span className="font-mono text-xs">{wallet.address}</span></p>
              <p><strong>Type:</strong> Platform-managed (keys stored encrypted)</p>
            </div>

            <DialogFooter>
              <Button variant="outline" onClick={handleClose}>Cancel</Button>
              <Button variant="destructive" onClick={handleExport} disabled={loading}>
                {loading ? 'Decrypting...' : 'I Understand — Show Keys'}
              </Button>
            </DialogFooter>
          </div>
        ) : (
          <div className="space-y-4">
            <div className="rounded-lg border border-green-200 bg-green-50 dark:border-green-900 dark:bg-green-950 p-2 text-xs text-green-700 dark:text-green-300">
              <p>🔒 Session only — keys will be cleared when you close this dialog.</p>
            </div>

            {/* Private Key */}
            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <Label className="text-sm font-semibold">Private Key</Label>
                <div className="flex gap-1">
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 w-7 p-0"
                    onClick={() => setShowPrivateKey(!showPrivateKey)}
                  >
                    {showPrivateKey ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 w-7 p-0"
                    onClick={() => handleCopy(exportData.privateKey, setCopiedKey)}
                  >
                    {copiedKey ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
                  </Button>
                </div>
              </div>
              <div className="relative">
                <Input
                  readOnly
                  value={exportData.privateKey}
                  type={showPrivateKey ? 'text' : 'password'}
                  className="font-mono text-xs pr-20"
                />
              </div>
            </div>

            {/* Seed Phrase */}
            {exportData.seedPhrase && (
              <div className="space-y-1.5">
                <div className="flex items-center justify-between">
                  <Label className="text-sm font-semibold">Seed Phrase (Mnemonic)</Label>
                  <div className="flex gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      className="h-7 w-7 p-0"
                      onClick={() => setShowSeedPhrase(!showSeedPhrase)}
                    >
                      {showSeedPhrase ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="h-7 w-7 p-0"
                      onClick={() => handleCopy(exportData.seedPhrase!, setCopiedSeed)}
                    >
                      {copiedSeed ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
                    </Button>
                  </div>
                </div>
                <textarea
                  readOnly
                  value={exportData.seedPhrase}
                  className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono text-xs resize-none"
                  rows={3}
                  style={{ filter: showSeedPhrase ? 'none' : 'blur(6px)', transition: 'filter 0.2s' }}
                />
              </div>
            )}

            <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-2 text-xs text-amber-700 dark:text-amber-300">
              <p>⚠️ Store these securely offline. Never share them. Close this dialog when done.</p>
            </div>

            <DialogFooter>
              <Button variant="outline" onClick={handleClose}>Close & Clear</Button>
            </DialogFooter>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}