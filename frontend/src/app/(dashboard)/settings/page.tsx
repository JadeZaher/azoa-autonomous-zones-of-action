'use client'

import { useState, useEffect } from 'react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useAzoa } from '@/lib/azoa-context'
import { azoa } from '@/lib/azoa'
import { JsonViewer } from '@/components/shared/json-viewer'

const PROVIDER_KEY = 'azoa_provider_name'

function maskToken(token: string | null): string {
  if (!token) return '—'
  if (token.length <= 16) return token
  return `${token.slice(0, 8)}…${token.slice(-8)}`
}

export default function SettingsPage() {
  const { avatarId, isAuthenticated, user, logout } = useAzoaAuth()
  const { logoutEverywhere } = useAzoa()
  const [logoutEverywhereError, setLogoutEverywhereError] = useState<string | null>(null)
  const [token, setToken] = useState<string | null>(null)
  const [providerName, setProviderName] = useState('')
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    if (typeof window !== 'undefined') {
      setToken(localStorage.getItem('azoa_token') ?? localStorage.getItem('token'))
      setProviderName(localStorage.getItem(PROVIDER_KEY) ?? '')
    }
  }, [])

  const handleSaveProvider = () => {
    if (typeof window !== 'undefined') localStorage.setItem(PROVIDER_KEY, providerName)
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleClearSession = async () => { await azoa.auth.logout(); await logout() }

  const handleLogoutEverywhere = async () => {
    setLogoutEverywhereError(null)
    const result = await logoutEverywhere()
    if (!result.success) setLogoutEverywhereError(result.error ?? 'Failed to log out of all devices')
  }

  const chainNames = azoa.wallet.chains
  const sessionState = { avatarId, isAuthenticated, user, tokenMasked: maskToken(token) }
  const apiBaseUrl = azoa.getApiUrl()
  const rpcUrls: Record<string, string> = {
    algorand: process.env.NEXT_PUBLIC_ALGO_RPC ?? 'https://testnet-api.algonode.cloud',
    solana: process.env.NEXT_PUBLIC_SOL_RPC ?? 'https://api.devnet.solana.com',
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted-foreground">Session, SDK config, and debug tools</p>
      </div>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Session</CardTitle></CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex justify-between"><span className="text-muted-foreground">Auth</span><Badge variant={isAuthenticated ? 'default' : 'secondary'} className="text-[10px]">{isAuthenticated ? 'Active' : 'None'}</Badge></div>
          <Separator />
          <div className="flex justify-between"><span className="text-muted-foreground">Avatar ID</span><span className="font-mono text-xs">{avatarId ?? '—'}</span></div>
          <Separator />
          <div className="flex justify-between items-center"><span className="text-muted-foreground">Token</span><span className="font-mono text-[11px] max-w-[55%] break-all text-right">{maskToken(token)}</span></div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">SDK Configuration</CardTitle><CardDescription>Registered chains and API endpoint</CardDescription></CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex justify-between"><span className="text-muted-foreground">API URL</span><span className="font-mono text-xs">{apiBaseUrl}</span></div>
          <Separator />
          <div className="flex justify-between items-center"><span className="text-muted-foreground">Chains</span>
            <div className="flex gap-1">{chainNames.length > 0 ? chainNames.map(c => <Badge key={c} variant="outline" className="text-[10px] capitalize">{c}</Badge>) : <span className="text-xs text-muted-foreground">none</span>}</div>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Network</CardTitle><CardDescription>RPC endpoints</CardDescription></CardHeader>
        <CardContent className="space-y-2 text-sm">
          {Object.entries(rpcUrls).map(([chain, url], i) => (
            <div key={chain}>{i > 0 && <Separator className="my-2" />}
              <div className="flex justify-between gap-4"><span className="text-muted-foreground capitalize">{chain}</span><span className="font-mono text-xs max-w-[65%] text-right break-all">{url}</span></div>
            </div>
          ))}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Provider Override</CardTitle><CardDescription>Set a provider name for AZOA requests. Informational only.</CardDescription></CardHeader>
        <CardContent className="space-y-3">
          <div className="flex gap-2">
            <Input value={providerName} onChange={e => setProviderName(e.target.value)} placeholder="e.g. MongoAZOA" className="max-w-xs" />
            <Button onClick={handleSaveProvider} variant="secondary" size="sm">{saved ? 'Saved' : 'Save'}</Button>
          </div>
        </CardContent>
      </Card>

      <Separator />

      <Card className="border-destructive/30">
        <CardHeader className="pb-3"><CardTitle className="text-sm text-destructive">Danger</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="destructive" size="sm" onClick={handleClearSession}>Clear session</Button>
            <Button variant="destructive" size="sm" onClick={handleLogoutEverywhere}>Log out of all devices</Button>
          </div>
          <p className="text-xs text-muted-foreground">
            "Log out of all devices" invalidates every active session token for this account, everywhere it's signed in.
          </p>
          {logoutEverywhereError && <p className="text-sm text-destructive">{logoutEverywhereError}</p>}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Raw Session</CardTitle></CardHeader>
        <CardContent><div className="rounded-md bg-muted/50 p-3 text-xs font-mono"><JsonViewer data={sessionState} /></div></CardContent>
      </Card>
    </div>
  )
}
