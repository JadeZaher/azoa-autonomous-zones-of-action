'use client'

import { useState, useCallback } from 'react'
import { toast } from 'sonner'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Separator } from '@/components/ui/separator'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter,
} from '@/components/ui/dialog'
import { ChainBadge } from '@/components/shared/chain-badge'
import { ErrorBanner } from '@/components/shared/error-banner'
import { WalletTypeBadge } from '@/components/wallet-type-badge'
import { BrowserWalletConnectDialog } from '@/components/browser-wallet-connect'
import { PlatformWalletGenerateDialog } from '@/components/platform-wallet-generate'
import { WalletExportDialog } from '@/components/wallet-export-dialog'
import { WalletFundDialog } from '@/components/wallet-fund-dialog'
import { useAzoa, type WalletEntry } from '@/lib/azoa-context'
import { usePortfolio } from '@/lib/azoa-hooks'
import { useNetwork } from '@/lib/network-context'
import type { ChainBalance } from '@/lib/azoa'
import { Wallet, ExternalLink, Key, Download, Trash2, Star, Coins, ShoppingCart } from 'lucide-react'

function truncateAddress(addr: string, chars = 6): string {
  return addr.length <= chars * 2 + 3 ? addr : `${addr.slice(0, chars)}…${addr.slice(-chars)}`
}

function PortfolioCard({ avatarId }: { avatarId: string }) {
  const { portfolio, loading, error, refresh } = usePortfolio(avatarId)
  return (
    <Card>
      <CardHeader className="pb-3"><CardTitle className="text-sm">Portfolio</CardTitle></CardHeader>
      <CardContent>
        {loading ? (
          <div className="space-y-2"><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-3/4" /></div>
        ) : error ? (
          <ErrorBanner message={error} onRetry={refresh} />
        ) : !portfolio ? (
          <p className="text-sm text-muted-foreground">No data.</p>
        ) : (
          <div className="space-y-3">
            <div className="flex justify-between text-sm">
              <span className="text-muted-foreground">Wallets</span>
              <span className="font-medium">{portfolio.walletCount}</span>
            </div>
            <Separator />
            {portfolio.chains?.length > 0 ? (
              <div className="space-y-2">
                <p className="text-[11px] font-semibold uppercase text-muted-foreground">Balances</p>
                {portfolio.chains.map((c: ChainBalance) => (
                  <div key={c.chain + c.address} className="flex items-center justify-between text-sm">
                    <ChainBadge chain={c.chain} />
                    <span className="font-mono text-xs">{c.balance.amount} {c.balance.symbol}</span>
                  </div>
                ))}
              </div>
            ) : <p className="text-sm text-muted-foreground">No balances.</p>}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function WalletActions({
  wallet,
  onSetDefault,
  onExport,
  onRemove,
  onFund,
  isTestLike,
  settingDefault,
}: {
  wallet: WalletEntry
  onSetDefault: (w: WalletEntry) => void
  onExport: (w: WalletEntry) => void
  onRemove: (w: WalletEntry) => void
  onFund: (w: WalletEntry) => void
  isTestLike: boolean
  settingDefault: string | null
}) {
  return (
    <div className="flex items-center justify-end gap-1">
      <Button
        variant="ghost"
        size="sm"
        className="h-7 w-7 p-0"
        onClick={() => onFund(wallet)}
        title={isTestLike ? 'Top up (test funds)' : 'Buy token'}
      >
        {isTestLike
          ? <Coins className="h-3.5 w-3.5" />
          : <ShoppingCart className="h-3.5 w-3.5" />}
      </Button>
      {!wallet.isDefault && (
        <Button
          variant="ghost"
          size="sm"
          className="h-7 w-7 p-0"
          disabled={settingDefault === wallet.id}
          onClick={() => onSetDefault(wallet)}
          title="Set as default"
        >
          <Star className="h-3.5 w-3.5" />
        </Button>
      )}
      {wallet.walletType === 'Platform' && (
        <Button
          variant="ghost"
          size="sm"
          className="h-7 w-7 p-0"
          onClick={() => onExport(wallet)}
          title="Export wallet (private key / seed phrase)"
        >
          <Download className="h-3.5 w-3.5" />
        </Button>
      )}
      <Button
        variant="ghost"
        size="sm"
        className="h-7 w-7 p-0 text-red-500 hover:text-red-600 hover:bg-red-50"
        onClick={() => onRemove(wallet)}
        title="Remove wallet"
      >
        <Trash2 className="h-3.5 w-3.5" />
      </Button>
    </div>
  )
}

function RemoveWalletDialog({
  wallet,
  open,
  onOpenChange,
  onConfirm,
}: {
  wallet: WalletEntry
  open: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: () => void
}) {
  const [loading, setLoading] = useState(false)
  const handleRemove = async () => {
    setLoading(true)
    await onConfirm()
    setLoading(false)
  }
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader><DialogTitle>Remove Wallet</DialogTitle></DialogHeader>
        <div className="space-y-3 text-sm">
          <p>Are you sure you want to remove this wallet?</p>
          <div className="rounded-lg bg-muted p-3 space-y-1">
            <p><strong>Chain:</strong> {wallet.chainType}</p>
            <p><strong>Address:</strong> <span className="font-mono text-xs">{truncateAddress(wallet.address, 8)}</span></p>
            <p><strong>Type:</strong> <WalletTypeBadge walletType={wallet.walletType} /></p>
          </div>
          {wallet.walletType === 'Platform' && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-2 text-xs text-amber-700 dark:text-amber-300">
              ⚠️ This is a platform-generated wallet. Ensure you have exported your private key/seed phrase before removing — it cannot be recovered.
            </div>
          )}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="destructive" onClick={handleRemove} disabled={loading}>
            {loading ? 'Removing...' : 'Remove'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export default function WalletsPage() {
  const {
    wallets, walletsLoading: loading, walletsError: error,
    refreshWallets: refresh, avatarId, setDefaultWallet, removeWallet,
  } = useAzoa()

  const { isTestLike } = useNetwork()
  const [settingDefault, setSettingDefault] = useState<string | null>(null)
  const [showBrowserConnect, setShowBrowserConnect] = useState(false)
  const [showPlatformGenerate, setShowPlatformGenerate] = useState(false)
  const [exportWalletTarget, setExportWalletTarget] = useState<WalletEntry | null>(null)
  const [removeWalletTarget, setRemoveWalletTarget] = useState<WalletEntry | null>(null)
  const [fundWalletTarget, setFundWalletTarget] = useState<WalletEntry | null>(null)

  const handleSetDefault = async (w: WalletEntry) => {
    setSettingDefault(w.id)
    const result = await setDefaultWallet(w.id)
    setSettingDefault(null)
    if (result !== undefined) toast.success('Default wallet updated')
    refresh()
  }

  const handleRemoveWallet = useCallback(async () => {
    if (!removeWalletTarget) return
    const result = await removeWallet(removeWalletTarget.id)
    if (result.success) {
      toast.success('Wallet removed')
      setRemoveWalletTarget(null)
    } else {
      toast.error(result.error ?? 'Failed to remove wallet')
    }
  }, [removeWalletTarget, removeWallet])

  // Counts
  const platformWallets = wallets.filter(w => w.walletType === 'Platform')
  const externalWallets = wallets.filter(w => w.walletType === 'External')

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-semibold tracking-tight">Wallets</h1>
          <p className="text-sm text-muted-foreground">
            Connect browser wallets (MetaMask, Phantom, Pera) or create platform-managed wallets
          </p>
        </div>
        {avatarId && (
          <div className="flex items-center gap-2">
            <Button size="sm" variant="outline" onClick={() => setShowBrowserConnect(true)}>
              <ExternalLink className="h-4 w-4 mr-1.5" />
              Connect Wallet
            </Button>
            <Button size="sm" onClick={() => setShowPlatformGenerate(true)}>
              <Key className="h-4 w-4 mr-1.5" />
              Create Wallet
            </Button>
          </div>
        )}
      </div>

      {/* Summary cards */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <Card>
          <CardContent className="pt-6">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 items-center justify-center rounded-full bg-blue-100 dark:bg-blue-900">
                <Wallet className="h-5 w-5 text-blue-600 dark:text-blue-300" />
              </div>
              <div>
                <p className="text-2xl font-bold">{wallets.length}</p>
                <p className="text-xs text-muted-foreground">Total Wallets</p>
              </div>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="pt-6">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 items-center justify-center rounded-full bg-green-100 dark:bg-green-900">
                <ExternalLink className="h-5 w-5 text-green-600 dark:text-green-300" />
              </div>
              <div>
                <p className="text-2xl font-bold">{externalWallets.length}</p>
                <p className="text-xs text-muted-foreground">External (Browser)</p>
              </div>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="pt-6">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-100 dark:bg-amber-900">
                <Key className="h-5 w-5 text-amber-600 dark:text-amber-300" />
              </div>
              <div>
                <p className="text-2xl font-bold">{platformWallets.length}</p>
                <p className="text-xs text-muted-foreground">Platform-Managed</p>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Main content */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm flex items-center justify-between">
                <span>Wallets {!loading && `(${wallets.length})`}</span>
              </CardTitle>
            </CardHeader>
            <CardContent className="p-0">
              {loading ? (
                <div className="space-y-2 p-4">
                  {[...Array(3)].map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}
                </div>
              ) : error ? (
                <div className="p-4"><ErrorBanner message={error} onRetry={refresh} /></div>
              ) : wallets.length === 0 ? (
                <div className="p-8 text-center space-y-3">
                  <Wallet className="h-10 w-10 text-muted-foreground/50 mx-auto" />
                  <p className="text-sm text-muted-foreground">No wallets yet.</p>
                  <div className="flex items-center justify-center gap-2">
                    <Button size="sm" variant="outline" onClick={() => setShowBrowserConnect(true)}>
                      <ExternalLink className="h-4 w-4 mr-1" />
                      Connect Browser Wallet
                    </Button>
                    <Button size="sm" onClick={() => setShowPlatformGenerate(true)}>
                      <Key className="h-4 w-4 mr-1" />
                      Create Platform Wallet
                    </Button>
                  </div>
                </div>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Address</TableHead>
                      <TableHead>Chain</TableHead>
                      <TableHead>Type</TableHead>
                      <TableHead>Label</TableHead>
                      <TableHead>Default</TableHead>
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {wallets.map(w => (
                      <TableRow key={w.id}>
                        <TableCell className="font-mono text-xs max-w-[180px] truncate">
                          {truncateAddress(w.address, 8)}
                        </TableCell>
                        <TableCell><ChainBadge chain={w.chainType} /></TableCell>
                        <TableCell><WalletTypeBadge walletType={w.walletType} /></TableCell>
                        <TableCell>
                          {w.label ?? <span className="text-muted-foreground text-xs">—</span>}
                        </TableCell>
                        <TableCell>
                          {w.isDefault ? (
                            <Badge variant="secondary" className="text-[10px]">Default</Badge>
                          ) : (
                            <span className="text-muted-foreground text-xs">—</span>
                          )}
                        </TableCell>
                        <TableCell>
                          <WalletActions
                            wallet={w}
                            onSetDefault={handleSetDefault}
                            onExport={setExportWalletTarget}
                            onRemove={setRemoveWalletTarget}
                            onFund={setFundWalletTarget}
                            isTestLike={isTestLike}
                            settingDefault={settingDefault}
                          />
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </div>
        <div>
          {avatarId ? <PortfolioCard avatarId={avatarId} /> : (
            <Card>
              <CardContent className="pt-6">
                <p className="text-sm text-muted-foreground text-center">Sign in to view portfolio.</p>
              </CardContent>
            </Card>
          )}
        </div>
      </div>

      {/* Dialogs */}
      <BrowserWalletConnectDialog
        open={showBrowserConnect}
        onOpenChange={setShowBrowserConnect}
      />
      <PlatformWalletGenerateDialog
        open={showPlatformGenerate}
        onOpenChange={setShowPlatformGenerate}
      />
      {exportWalletTarget && (
        <WalletExportDialog
          wallet={exportWalletTarget}
          open={true}
          onOpenChange={(open) => { if (!open) setExportWalletTarget(null) }}
        />
      )}
      {removeWalletTarget && (
        <RemoveWalletDialog
          wallet={removeWalletTarget}
          open={true}
          onOpenChange={(open) => { if (!open) setRemoveWalletTarget(null) }}
          onConfirm={handleRemoveWallet}
        />
      )}
      {fundWalletTarget && (
        <WalletFundDialog
          wallet={fundWalletTarget}
          open={true}
          onOpenChange={(open) => { if (!open) setFundWalletTarget(null) }}
        />
      )}
    </div>
  )
}