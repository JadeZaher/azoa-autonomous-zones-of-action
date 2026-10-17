'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { DexAsset, DexCapabilityView, DexQuoteView, EthereumPreparationView, SwapExternalSigningView, SwapOperationView } from 'azoa-sdk'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useWallets } from '@/lib/azoa-hooks'

/** Durable swap session saved before admission so a lost response replays the identical body. */
interface SavedSwap {
  requestKey: string; quoteId: string; quoteHash: string; provider: string; walletId: string; networkKey: string
  inputAssetId: string; outputAssetId: string; operationId?: string
}

const CLEANUP: Record<string, { stage: string; revoke: boolean }> = {
  Tinyman: { stage: 'CloseOptIn', revoke: false },
  Jupiter: { stage: 'CloseAta', revoke: false },
  UniswapV3: { stage: 'RevokeAllowance', revoke: true },
}

const networkKey = (view: DexCapabilityView) => `${view.network.chainType}:${view.network.network}`
const storageKey = (userId: string) => `azoa:dex-swap:${userId}`

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="flex justify-between gap-4 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-mono break-all text-right">{value ?? '—'}</span>
    </div>
  )
}

export default function SwapPage() {
  const { userId } = useAzoaAuth()
  const { wallets } = useWallets(userId)
  const [capabilities, setCapabilities] = useState<DexCapabilityView[] | null>(null)
  const [selected, setSelected] = useState('')
  const [walletId, setWalletId] = useState('')
  const [input, setInput] = useState<DexAsset>({ id: '', symbol: '', decimals: 0, native: false })
  const [output, setOutput] = useState<DexAsset>({ id: '', symbol: '', decimals: 0, native: false })
  const [grossInput, setGrossInput] = useState('')
  const [slippageBps, setSlippageBps] = useState('50')
  const [budget, setBudget] = useState({ maximumNetworkFee: '', maximumLockedBalance: '0', maximumCleanupFee: '' })
  const [quote, setQuote] = useState<DexQuoteView | null>(null)
  const [unavailable, setUnavailable] = useState<string | null>(null)
  const [accept, setAccept] = useState(false)
  const [operation, setOperation] = useState<SwapOperationView | null>(null)
  const [saved, setSaved] = useState<SavedSwap | null>(null)
  const [exposure, setExposure] = useState<SwapExternalSigningView | null>(null)
  const [exposureConsent, setExposureConsent] = useState(false)
  const [signedEnvelope, setSignedEnvelope] = useState('')
  const [cleanupFee, setCleanupFee] = useState('')
  const [cleanupConsent, setCleanupConsent] = useState(false)
  const [sequenceFee, setSequenceFee] = useState('')
  const [sequenceReason, setSequenceReason] = useState('')
  const [stageReservation, setStageReservation] = useState<EthereumPreparationView | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const generation = useRef(0)

  const capability = useMemo(() => capabilities?.find((view) => networkKey(view) === selected) ?? null, [capabilities, selected])
  const ownedWallets = wallets.filter((wallet) => capability && wallet.chainType.toLowerCase() === capability.network.chainType.toLowerCase())

  useEffect(() => {
    void azoa.api.getDexCapabilities().then((result) => {
      if (isOk(result)) setCapabilities(result.value)
      else { setCapabilities([]); setError(result.error.message) }
    })
  }, [])

  useEffect(() => {
    if (!userId) return
    const raw = localStorage.getItem(storageKey(userId))
    if (!raw) return
    try {
      const restored = JSON.parse(raw) as SavedSwap
      setSaved(restored)
      if (restored.operationId) void azoa.api.getDexSwap(restored.operationId)
        .then((result) => { if (isOk(result)) setOperation(result.value) })
        .catch(() => localStorage.removeItem(storageKey(userId)))
    } catch { localStorage.removeItem(storageKey(userId)) }
  }, [userId])

  // A cancelled Ethereum stage may still own its nonce at the allocator even when the journal saved no signature
  // (crash between custody signing and save, or an interrupted release): read the allocator row itself.
  const reservationKey = saved?.provider === 'UniswapV3' && operation?.currentStage?.state === 'Cancelled'
    ? operation.currentStage.preparationKey ?? null : null
  const reservationNetwork = operation?.network.network
  useEffect(() => {
    if (!reservationKey || !reservationNetwork) { setStageReservation(null); return }
    let current = true
    void azoa.api.listEthereumPreparations(reservationNetwork as 'Devnet' | 'Testnet' | 'Mainnet').then((listed) => {
      if (current) setStageReservation(isOk(listed) ? listed.value.find((item) => item.preparationKey === reservationKey) ?? null : null)
    })
    return () => { current = false }
  }, [reservationKey, reservationNetwork, operation?.version])

  // Any scope change starts a new generation so late responses cannot project into another account or network.
  const changeScope = (apply: () => void) => {
    generation.current += 1
    setQuote(null); setUnavailable(null); setAccept(false); setError(null)
    apply()
  }

  const persist = (value: SavedSwap | null) => {
    if (!userId) return
    if (value) localStorage.setItem(storageKey(userId), JSON.stringify(value))
    else localStorage.removeItem(storageKey(userId))
    setSaved(value)
  }

  const run = useCallback(async <T,>(work: () => Promise<{ ok: true; value: T } | { ok: false; error: { message: string } }>, apply: (value: T) => void) => {
    const mine = generation.current
    setBusy(true); setError(null)
    try {
      const result = await work()
      if (mine !== generation.current) return
      if (result.ok) apply(result.value)
      else setError(result.error.message)
    } finally { setBusy(false) }
  }, [])

  const requestQuote = () => capability && run(() => azoa.api.quoteDex({
    walletId, network: capability.network, inputAsset: input, outputAsset: output, grossInput,
    slippageBps: Number(slippageBps), budget,
  }), (availability) => {
    setQuote(availability.quote ?? null)
    setUnavailable(availability.unavailable ? availability.reason ?? 'No reviewed liquidity is available.' : null)
  })

  const admit = (session: SavedSwap) => run(() => azoa.api.startDexSwap({
    quoteId: session.quoteId, quoteHash: session.quoteHash, requestKey: session.requestKey, acceptEconomicEffects: true,
  }), (view) => { setOperation(view); persist({ ...session, operationId: view.operationId }) })

  const startSwap = () => {
    if (!quote || !accept || !capability) return
    const session: SavedSwap = { requestKey: crypto.randomUUID(), quoteId: quote.quoteId, quoteHash: quote.quoteHash,
      provider: quote.terms.provider, walletId, networkKey: selected, inputAssetId: quote.terms.inputAsset.id,
      outputAssetId: quote.terms.outputAsset.id }
    persist(session)
    void admit(session)
  }

  const stageCommand = () => operation?.currentStage
    ? { expectedVersion: operation.version, stageId: operation.currentStage.stageId, expectedStageVersion: operation.currentStage.version }
    : null

  const cleanup = saved ? CLEANUP[saved.provider] : undefined
  // A signed-but-unsent or disclosed Ethereum stage still owns its nonce; only a same-nonce cancellation frees the wallet.
  // The allocator row is authoritative for custody signatures the journal never saved ('Prepared' = signed, unsent).
  const sequenceHeld = saved?.provider === 'UniswapV3' && !!operation?.currentStage?.preparationKey && (
    (operation.currentStage.state === 'Cancelled' && (!!operation.currentStage.signedCandidateSaved || stageReservation?.state === 'Prepared'))
    || (operation.currentStage.externalSigningExposed && operation.currentStage.state === 'ExternalExposed'))
  // A never-signed reservation is handed back for reuse by repeating the (idempotent) cancel; no network fee.
  const unsignedHole = !sequenceHeld && stageReservation?.state === 'Reserved' && !stageReservation.transactionId

  const cancelReservedNonce = async () => {
    if (!operation?.currentStage?.preparationKey) return
    const network = operation.network.network as 'Devnet' | 'Testnet' | 'Mainnet'
    const key = operation.currentStage.preparationKey
    await run(async () => {
      const listed = await azoa.api.listEthereumPreparations(network)
      if (!listed.ok) return listed
      const reservation = listed.value.find((item) => item.preparationKey === key)
      if (!reservation) return { ok: false as const, error: { message: 'The reserved nonce for this stage is not in your recovery list.' } }
      return azoa.api.recoverEthereumPreparation(network, reservation.id, { cancel: true, maximumFeePerGas: sequenceFee, reason: sequenceReason })
    }, () => setError(null))
  }
  const settled = operation?.state === 'Completed' || operation?.retentionReconciled
  const stage = operation?.currentStage

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Swap</h1>
        <p className="text-sm text-muted-foreground">
          Exact-input swaps through the durable DEX journal. A swap is complete only after finalized economic settlement.
        </p>
      </div>

      {error ? <ResultDisplay isError message={error} /> : null}

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Reviewed DEX modules</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          {capabilities === null ? <p className="text-sm text-muted-foreground">Loading capabilities…</p> : null}
          {capabilities?.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No DEX module is enabled on this node. Swap execution stays unavailable until a reviewed module is activated.
            </p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            {capabilities?.map((view) => (
              <Button key={networkKey(view)} size="sm" variant={selected === networkKey(view) ? 'default' : 'outline'} disabled={!view.ready}
                onClick={() => changeScope(() => { setSelected(networkKey(view)); setWalletId('') })} title={view.detail ?? undefined}>
                {view.capability.provider} · {view.network.chainType} {view.network.network}{view.ready ? '' : ' (unavailable)'}
              </Button>
            ))}
          </div>
          {capability ? (
            <ul className="list-disc pl-5 text-xs text-muted-foreground space-y-1">
              {capability.capability.restrictions.map((restriction) => <li key={restriction}>{restriction}</li>)}
            </ul>
          ) : null}
        </CardContent>
      </Card>

      {capability ? (
        <Card>
          <CardHeader className="pb-3"><CardTitle className="text-sm">Exact-input request</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="swap-wallet">Owned wallet</Label>
              <select id="swap-wallet" className="w-full rounded-md border bg-background p-2 text-sm" value={walletId}
                onChange={(event) => changeScope(() => setWalletId(event.target.value))}>
                <option value="">Select a {capability.network.chainType} wallet</option>
                {ownedWallets.map((wallet) => <option key={wallet.id} value={wallet.id}>{wallet.label ?? wallet.address}</option>)}
              </select>
            </div>
            {([['Input', input, setInput], ['Output', output, setOutput]] as const).map(([name, asset, update]) => (
              <div key={name} className="grid gap-2 sm:grid-cols-4">
                <Input placeholder={`${name} asset id`} value={asset.id} onChange={(event) => changeScope(() => update({ ...asset, id: event.target.value }))} />
                <Input placeholder="Symbol" value={asset.symbol} onChange={(event) => changeScope(() => update({ ...asset, symbol: event.target.value }))} />
                <Input placeholder="Decimals" inputMode="numeric" value={String(asset.decimals)}
                  onChange={(event) => changeScope(() => update({ ...asset, decimals: Number(event.target.value) || 0 }))} />
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={asset.native} onChange={(event) => changeScope(() => update({ ...asset, native: event.target.checked }))} />
                  Native
                </label>
              </div>
            ))}
            <div className="grid gap-2 sm:grid-cols-2">
              <Input placeholder="Gross input (base units)" inputMode="numeric" value={grossInput} onChange={(event) => changeScope(() => setGrossInput(event.target.value))} />
              <Input placeholder="Slippage (bps)" inputMode="numeric" value={slippageBps} onChange={(event) => changeScope(() => setSlippageBps(event.target.value))} />
            </div>
            <div className="grid gap-2 sm:grid-cols-3">
              <Input placeholder="Max network fee" value={budget.maximumNetworkFee} onChange={(event) => changeScope(() => setBudget({ ...budget, maximumNetworkFee: event.target.value }))} />
              <Input placeholder="Max locked balance (rent)" value={budget.maximumLockedBalance} onChange={(event) => changeScope(() => setBudget({ ...budget, maximumLockedBalance: event.target.value }))} />
              <Input placeholder="Max cleanup fee" value={budget.maximumCleanupFee} onChange={(event) => changeScope(() => setBudget({ ...budget, maximumCleanupFee: event.target.value }))} />
            </div>
            <Button onClick={() => void requestQuote()} disabled={busy || !walletId || !input.id || !output.id || !grossInput}>Get immutable quote</Button>
            {unavailable ? <p className="text-sm text-muted-foreground">Quote unavailable: {unavailable}</p> : null}
          </CardContent>
        </Card>
      ) : null}

      {quote ? (
        <Card className="border-blue-500/40">
          <CardHeader className="pb-2"><CardTitle className="text-sm">Quote {quote.quoteHash.slice(0, 12)}… · {quote.terms.provider}</CardTitle></CardHeader>
          <CardContent className="space-y-2">
            <Row label="Gross debit" value={quote.terms.grossInput} />
            <Row label="Platform fee (input units)" value={quote.terms.platformFee.amount} />
            <Row label="Net swap input" value={quote.terms.swapInput} />
            <Separator />
            <Row label="Expected output" value={quote.terms.expectedOutput} />
            <Row label="Minimum output (consented floor)" value={quote.terms.minimumOutput} />
            <Row label="Network fee cap" value={quote.terms.budget.maximumNetworkFee} />
            <Row label="Locked balance cap" value={quote.terms.budget.maximumLockedBalance} />
            <Row label="Quote admission expires" value={quote.terms.expiresAt} />
            {quote.terms.prerequisites.length > 0 ? (
              <div className="space-y-1 pt-2">
                <p className="text-xs text-muted-foreground">Prerequisites retained even if the swap fails:</p>
                {quote.terms.prerequisites.map((effect, index) => (
                  <Badge key={index} variant="secondary" className="mr-1 font-mono text-xs">{effect.kind} {effect.amount} {effect.assetId}</Badge>
                ))}
              </div>
            ) : null}
            <label className="flex items-center gap-2 pt-2 text-sm">
              <input type="checkbox" checked={accept} onChange={(event) => setAccept(event.target.checked)} />
              I accept the gross debit, fee, minimum output, retained prerequisites and cost caps above.
            </label>
            <Button className="w-full" disabled={busy || !accept} onClick={startSwap}>Admit swap</Button>
          </CardContent>
        </Card>
      ) : null}

      {saved && !operation ? (
        <Card>
          <CardContent className="space-y-2 pt-4">
            <p className="text-sm">A saved admission has no confirmed response yet. Retrying replays the identical request.</p>
            <div className="flex gap-2">
              <Button size="sm" disabled={busy} onClick={() => void admit(saved)}>Retry original admission</Button>
              <Button size="sm" variant="outline" onClick={() => persist(null)}>Forget saved request</Button>
            </div>
          </CardContent>
        </Card>
      ) : null}

      {operation ? (
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm">Operation {operation.operationId.slice(0, 12)}… <Badge variant="outline">{operation.state}</Badge></CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <Row label="Stage" value={stage ? `${stage.stage} (${stage.purpose}) · ${stage.state}` : 'none'} />
            <Row label="Actual input" value={operation.actualInput} />
            <Row label="Actual output" value={operation.actualOutput} />
            <Row label="Transactions" value={operation.transactionIds.join(', ') || null} />
            {operation.accounting ? (
              <>
                <Row label="Execution network fees" value={operation.accounting.confirmedExecutionNetworkFee} />
                <Row label="Cleanup network fees" value={operation.accounting.confirmedCleanupNetworkFee} />
                <Row label="Rent allocated / released" value={`${operation.accounting.confirmedRentAllocated} / ${operation.accounting.confirmedRentReleased}`} />
                <Row label="Unresolved rent reservation" value={operation.accounting.unresolvedRentReservation} />
                <Row label="Accounting complete" value={operation.accounting.complete ? 'yes' : 'no'} />
              </>
            ) : null}
            {operation.retainedEffects.length > 0 ? (
              <div className="space-y-1">
                <p className="text-xs text-muted-foreground">Retained effects (not rolled back):</p>
                {operation.retainedEffects.map((effect, index) => (
                  <Badge key={index} variant="secondary" className="mr-1 font-mono text-xs">{effect.kind} {effect.amount} {effect.assetId}</Badge>
                ))}
              </div>
            ) : null}
            {operation.detail ? <p className="text-xs text-muted-foreground">{operation.detail}</p> : null}
            <div className="flex flex-wrap gap-2">
              {['Admitted', 'StageResolved'].includes(operation.state) && !settled ? (
                <Button size="sm" disabled={busy} onClick={() => void run(() => azoa.api.prepareDexSwap(operation.operationId, { expectedVersion: operation.version }), setOperation)}>Prepare next stage</Button>
              ) : null}
              {stage?.state === 'Planned' && !stage.externalSigningExposed ? (
                <Button size="sm" disabled={busy} onClick={() => { const command = stageCommand(); if (command) void run(() => azoa.api.signDexSwap(operation.operationId, command), setOperation) }}>Sign with custody</Button>
              ) : null}
              {stage?.state === 'Prepared' && !stage.submissionAuthorized ? (
                <Button size="sm" disabled={busy} onClick={() => { const command = stageCommand(); if (command) void run(() => azoa.api.submitDexSwap(operation.operationId, command), setOperation) }}>Authorize submission</Button>
              ) : null}
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => azoa.api.refreshDexSwap(operation.operationId), setOperation)}>Refresh</Button>
              {operation.state === 'Admitted' || (operation.state === 'Preparing' && !stage?.externalSigningExposed && !stage?.submissionAuthorized) ? (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => azoa.api.cancelDexSwap(operation.operationId, { expectedVersion: operation.version }), setOperation)}>Cancel unsent</Button>
              ) : null}
              {stage?.submissionAuthorized && !stage.serverSubmissionWithdrawn ? (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => { const command = stageCommand(); if (command) void run(() => azoa.api.withdrawDexSwapSubmission(operation.operationId, command), setOperation) }}>Withdraw future server submission</Button>
              ) : null}
              {['Completed', 'Cancelled', 'CleanupCompleted'].includes(operation.state) ? (
                <Button size="sm" variant="ghost" onClick={() => { persist(null); setOperation(null) }}>Clear saved swap</Button>
              ) : null}
            </div>

            {stage?.state === 'Planned' ? (
              <div className="space-y-2 rounded-md border p-3">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={exposureConsent} onChange={(event) => setExposureConsent(event.target.checked)} />
                  Disclose the exact unsigned stage for my own wallet. Disclosure counts as potential submission and cannot be cancelled as unsent.
                </label>
                <Button size="sm" disabled={busy || !exposureConsent} onClick={() => { const command = stageCommand(); if (command) void run(() => azoa.api.exposeDexSwap(operation.operationId, { ...command, acceptPotentialExternalSubmission: true }), setExposure) }}>Expose for external signing</Button>
              </div>
            ) : null}
            {exposure && stage?.externalSigningExposed ? (
              <div className="space-y-2 rounded-md border p-3">
                <p className="text-xs text-muted-foreground">Sign exactly these bytes with {exposure.requiredSignerAddresses.join(', ')}; upload validates them without authorizing server submission.</p>
                <div className="rounded-md bg-muted p-2 text-xs"><JsonViewer data={exposure.unsignedPayloadJson} /></div>
                <Input placeholder="Signed envelope (JSON)" value={signedEnvelope} onChange={(event) => setSignedEnvelope(event.target.value)} />
                <Button size="sm" disabled={busy || !signedEnvelope} onClick={() => { const command = stageCommand(); if (command) void run(() => azoa.api.acceptDexSwapSignature(operation.operationId, { ...command, signedPayloadJson: signedEnvelope }), setOperation) }}>Upload signed envelope</Button>
              </div>
            ) : null}

            {cleanup && ['Completed', 'StageResolved', 'Cancelled', 'CleanupReady'].includes(operation.state) && operation.retainedEffects.length > 0 ? (
              <div className="space-y-2 rounded-md border p-3">
                <p className="text-sm font-medium">{cleanup.revoke ? 'Revoke the current allowance' : 'Close the current zero holding'}</p>
                <Input placeholder="Cleanup fee cap" value={cleanupFee} onChange={(event) => setCleanupFee(event.target.value)} />
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={cleanupConsent} onChange={(event) => setCleanupConsent(event.target.checked)} />
                  {cleanup.revoke
                    ? 'Set my current allowance for the input token and quoted executor to zero, including changes made outside this swap.'
                    : 'Close my current zero holding of the output asset, including a holding I closed and reopened.'}
                </label>
                <div className="flex gap-2">
                  <Button size="sm" disabled={busy || !cleanupConsent || !cleanupFee || operation.state === 'CleanupReady'} onClick={() => void run(() => azoa.api.beginDexSwapCleanup(operation.operationId, {
                    expectedVersion: operation.version, requestKey: `${saved!.requestKey}:cleanup`, maximumNetworkFee: cleanupFee,
                    assetId: cleanup.revoke ? saved!.inputAssetId : saved!.outputAssetId,
                    acceptCurrentZeroHoldingReplacement: !cleanup.revoke, acceptCurrentAllowanceRevocation: cleanup.revoke,
                  }), setOperation)}>Record cleanup consent</Button>
                  {operation.state === 'CleanupReady' ? (
                    <Button size="sm" disabled={busy} onClick={() => void run(() => azoa.api.prepareDexSwapCleanup(operation.operationId, { expectedVersion: operation.version, stage: cleanup.stage }), setOperation)}>Prepare cleanup</Button>
                  ) : null}
                </div>
              </div>
            ) : null}
            {unsignedHole && operation ? (
              <div className="space-y-2 rounded-md border border-amber-500/40 p-3">
                <p className="text-sm font-medium">This cancelled stage still reserves an unused Ethereum nonce</p>
                <p className="text-xs text-muted-foreground">
                  Nothing was signed for it. Releasing hands the nonce back so later transactions from this wallet are not held up.
                </p>
                <Button size="sm" variant="outline" disabled={busy}
                  onClick={() => void run(() => azoa.api.cancelDexSwap(operation.operationId, { expectedVersion: operation.version }), setOperation)}>
                  Release the unused nonce
                </Button>
              </div>
            ) : null}
            {sequenceHeld ? (
              <div className="space-y-2 rounded-md border border-amber-500/40 p-3">
                <p className="text-sm font-medium">This stage still holds its Ethereum nonce</p>
                <p className="text-xs text-muted-foreground">
                  Later transactions from this wallet wait behind it. A same-nonce cancellation spends a small network fee and makes
                  the saved or disclosed swap transaction permanently unexecutable; the swap then resolves as no effect.
                </p>
                <Input placeholder="Maximum fee per gas (wei)" value={sequenceFee} onChange={(event) => setSequenceFee(event.target.value)} />
                <Input placeholder="Reason (recorded with the cancellation)" value={sequenceReason} onChange={(event) => setSequenceReason(event.target.value)} />
                <Button size="sm" variant="outline" disabled={busy || !sequenceFee || sequenceReason.trim().length === 0}
                  onClick={() => void cancelReservedNonce()}>Cancel the reserved nonce</Button>
              </div>
            ) : null}
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}
