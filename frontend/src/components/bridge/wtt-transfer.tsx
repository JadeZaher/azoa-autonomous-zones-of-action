'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import type {
  WttChainBudget, WttCleanupOffer, WttNativeOutputOffer, WttOfferView, WttOperationView, WttParticipantSigningView, WttStageView,
} from 'azoa-sdk'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useWallets } from '@/lib/azoa-hooks'

/** Saved before start so a lost response or cleared tab replays the identical offer under the same request key. */
interface SavedTransfer { requestKey: string; offerId: string; offerHash: string; operationId: string }

const CHAINS = ['Ethereum', 'Solana', 'Algorand']
const storageKey = (userId: string) => `azoa:wtt-transfer:${userId}`
const emptyBudget: WttChainBudget = { maximumNetworkFee: '', maximumLockedDeposit: '0', maximumValue: '0' }

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="flex justify-between gap-4 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-mono break-all text-right">{value ?? '—'}</span>
    </div>
  )
}

function BudgetFields({ title, value, onChange }: { title: string; value: WttChainBudget; onChange: (next: WttChainBudget) => void }) {
  return (
    <div className="space-y-1">
      <Label className="text-xs">{title} (base units of that chain's fee currency)</Label>
      <div className="grid grid-cols-3 gap-2">
        <Input placeholder="Network fee cap" value={value.maximumNetworkFee} onChange={(event) => onChange({ ...value, maximumNetworkFee: event.target.value })} />
        <Input placeholder="Locked deposit cap" value={value.maximumLockedDeposit} onChange={(event) => onChange({ ...value, maximumLockedDeposit: event.target.value })} />
        <Input placeholder="Native value cap" value={value.maximumValue} onChange={(event) => onChange({ ...value, maximumValue: event.target.value })} />
      </div>
    </div>
  )
}

/** Wormhole Token Transfer journey; states, costs and every transaction come from the server receipt. */
export function WttTransferPanel() {
  const { userId } = useAzoaAuth()
  const { wallets } = useWallets(userId)
  const [form, setForm] = useState({ sourceChain: 'Algorand', sourceNetwork: 'Testnet', sourceWallet: '', tokenAddress: '', nativeAsset: false,
    amount: '', targetChain: 'Solana', targetNetwork: 'Testnet', targetWallet: '', recipient: '', unwrapNative: false })
  const [sourceBudget, setSourceBudget] = useState<WttChainBudget>(emptyBudget)
  const [targetBudget, setTargetBudget] = useState<WttChainBudget>(emptyBudget)
  const [offer, setOffer] = useState<WttOfferView | null>(null)
  const [accept, setAccept] = useState(false)
  const [saved, setSaved] = useState<SavedTransfer | null>(null)
  const [operation, setOperation] = useState<WttOperationView | null>(null)
  const [participant, setParticipant] = useState<WttParticipantSigningView | null>(null)
  const [exposureConsent, setExposureConsent] = useState(false)
  const [signed, setSigned] = useState('')
  const [native, setNative] = useState<WttNativeOutputOffer | null>(null)
  const [nativeConsent, setNativeConsent] = useState(false)
  const [cleanup, setCleanup] = useState<WttCleanupOffer | null>(null)
  const [cleanupConsent, setCleanupConsent] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const generation = useRef(0)

  const persist = useCallback((value: SavedTransfer | null) => {
    if (!userId) return
    if (value) localStorage.setItem(storageKey(userId), JSON.stringify(value))
    else localStorage.removeItem(storageKey(userId))
    setSaved(value)
  }, [userId])

  useEffect(() => {
    if (!userId) return
    const raw = localStorage.getItem(storageKey(userId))
    if (!raw) return
    try {
      const restored = JSON.parse(raw) as SavedTransfer
      setSaved(restored)
      void azoa.api.findWttByRequestKey(restored.requestKey).then((result) => { if (isOk(result)) setOperation(result.value) })
    } catch { localStorage.removeItem(storageKey(userId)) }
  }, [userId])

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

  const update = (patch: Partial<typeof form>) => { generation.current += 1; setOffer(null); setAccept(false); setForm({ ...form, ...patch }) }

  const requestQuote = () => {
    const requestKey = crypto.randomUUID()
    void run(() => azoa.api.quoteWtt({
      requestKey, source: { chainType: form.sourceChain, network: form.sourceNetwork, walletId: form.sourceWallet },
      tokenAddress: form.tokenAddress, nativeAsset: form.nativeAsset, amount: form.amount,
      target: { chainType: form.targetChain, network: form.targetNetwork, walletId: form.targetWallet }, recipientAddress: form.recipient,
      unwrapNative: form.unwrapNative, sourceBudget, targetBudget,
    }), (value) => { setOffer(value); setSaved({ requestKey, offerId: value.offerId, offerHash: value.offerHash, operationId: value.operationId }) })
  }

  const start = (session: SavedTransfer) => run(() => azoa.api.startWtt({ offerId: session.offerId, offerHash: session.offerHash,
    acceptEconomicEffects: true }, session.requestKey), setOperation)

  const stageOf = (view: WttOperationView | null): WttStageView | undefined =>
    view?.stages.find((stage) => stage.stageId === view.currentStageId)
  const current = stageOf(operation)
  const command = () => operation && current
    ? { expectedVersion: operation.version, stageId: current.stageId, expectedStageVersion: current.version } : null

  const walletOptions = (chain: string) => wallets.filter((wallet) => wallet.chainType.toLowerCase() === chain.toLowerCase())

  return (
    <Card>
      <CardHeader className="pb-2">
        <CardTitle className="text-sm">Wormhole token transfer</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {!operation ? (
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              {(['source', 'target'] as const).map((side) => (
                <div key={side} className="space-y-1">
                  <Label className="text-xs">{side === 'source' ? 'From' : 'To'}</Label>
                  <select className="w-full rounded-md border bg-background p-2 text-sm" value={side === 'source' ? form.sourceChain : form.targetChain}
                    onChange={(event) => update(side === 'source' ? { sourceChain: event.target.value, sourceWallet: '' } : { targetChain: event.target.value, targetWallet: '' })}>
                    {CHAINS.map((chain) => <option key={chain} value={chain}>{chain}</option>)}
                  </select>
                  <Input placeholder="Network (Devnet, Testnet, Mainnet)" value={side === 'source' ? form.sourceNetwork : form.targetNetwork}
                    onChange={(event) => update(side === 'source' ? { sourceNetwork: event.target.value } : { targetNetwork: event.target.value })} />
                  <select className="w-full rounded-md border bg-background p-2 text-sm" value={side === 'source' ? form.sourceWallet : form.targetWallet}
                    onChange={(event) => update(side === 'source' ? { sourceWallet: event.target.value } : { targetWallet: event.target.value })}>
                    <option value="">{side === 'source' ? 'Sending wallet' : 'Destination fee payer wallet'}</option>
                    {walletOptions(side === 'source' ? form.sourceChain : form.targetChain).map((wallet) => (
                      <option key={wallet.id} value={wallet.id}>{wallet.label ?? wallet.address}</option>
                    ))}
                  </select>
                </div>
              ))}
            </div>
            <Input placeholder="Token address or asset id (blank for the chain's native asset)" value={form.tokenAddress} onChange={(event) => update({ tokenAddress: event.target.value })} />
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.nativeAsset} onChange={(event) => update({ nativeAsset: event.target.checked })} />Send the chain's native asset</label>
            <Input placeholder="Amount (base units)" value={form.amount} onChange={(event) => update({ amount: event.target.value })} />
            <Input placeholder="Recipient address on the destination chain (apps acting for you may only use your own wallets)" value={form.recipient} onChange={(event) => update({ recipient: event.target.value })} />
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.unwrapNative} onChange={(event) => update({ unwrapNative: event.target.checked })} />Deliver as the destination's native asset (needs the recipient's own signature later)</label>
            <BudgetFields title="Source chain budget" value={sourceBudget} onChange={(value) => { generation.current += 1; setOffer(null); setSourceBudget(value) }} />
            <BudgetFields title="Destination chain budget" value={targetBudget} onChange={(value) => { generation.current += 1; setOffer(null); setTargetBudget(value) }} />
            <Button size="sm" disabled={busy || !form.sourceWallet || !form.targetWallet || !form.amount || !form.recipient} onClick={requestQuote}>Get offer</Button>
          </div>
        ) : null}

        {saved && !offer && !operation ? (
          <div className="space-y-2 rounded-md border p-3">
            <p className="text-xs text-muted-foreground">A transfer request was saved before its start response arrived. Retry the identical start, or cancel it so a delayed start can never be admitted.</p>
            <div className="flex gap-2">
              <Button size="sm" disabled={busy} onClick={() => void start(saved)}>Retry saved start</Button>
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => azoa.api.cancelWtt({ offerId: saved.offerId, offerHash: saved.offerHash }, saved.requestKey), setOperation)}>Cancel saved request</Button>
            </div>
          </div>
        ) : null}

        {offer && !operation ? (
          <div className="space-y-2 rounded-md border p-3">
            <Row label="Requested" value={offer.intent.amount.requested} />
            <Row label="Debited after protocol rounding" value={offer.intent.amount.debited} />
            <Row label="Dust kept in your wallet" value={offer.intent.amount.dust} />
            <Row label="Recipient receives (protocol units)" value={offer.intent.amount.recipientAmount} />
            <Row label="Destination representation" value={offer.target.registered ? offer.target.assetAddress ?? 'registered' : 'will be registered from an attestation'} />
            {offer.sourceQuote.stages.map((stage) => (
              <Row key={stage.stage} label={`Source ${stage.stage}`} value={`fee ≤ ${stage.networkFee} · deposit ${stage.lockedDeposit} · value ${stage.value}`} />
            ))}
            <p className="text-xs text-muted-foreground">Destination stages are priced after the guardian proof exists and must stay within the destination budget above. Fees and deposits are never reversed by cancelling.</p>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={accept} onChange={(event) => setAccept(event.target.checked)} />I accept these exact amounts and both chain budgets.</label>
            <Button size="sm" disabled={busy || !accept || !saved} onClick={() => { if (saved) { persist(saved); void start(saved) } }}>Start transfer</Button>
          </div>
        ) : null}

        {operation ? (
          <div className="space-y-3">
            <div className="flex items-center gap-2 text-sm">
              <span className="font-mono">{operation.operationId.slice(0, 12)}…</span>
              <Badge variant="outline">{operation.publicState}</Badge>
              <span className="text-muted-foreground">{operation.phase}</span>
            </div>
            <Row label="Source sequence" value={operation.transferSequence} />
            <Row label="Guardian proof verified" value={operation.proofVerified ? 'yes' : 'no'} />
            {operation.settlement ? (
              <>
                <Row label="Redemption transaction" value={operation.settlement.targetTransactionId} />
                <Row label="Recipient amount" value={operation.settlement.recipientAmount} />
                <Row label="Native output transaction" value={operation.settlement.nativeOutputTransactionId} />
              </>
            ) : null}
            {operation.detail ? <p className="text-xs text-muted-foreground">{operation.detail}</p> : null}
            <table className="w-full text-xs">
              <thead><tr className="text-left text-muted-foreground"><th>Stage</th><th>Try</th><th>State</th><th>Transaction</th><th>Fee paid</th><th /></tr></thead>
              <tbody>
                {operation.stages.map((stage) => (
                  <tr key={stage.stageId}>
                    <td>{stage.phase} · {stage.stage}</td><td>{stage.generation}</td><td>{stage.state}</td>
                    <td className="font-mono break-all">{stage.transactionId ?? '—'}</td><td>{stage.networkFeePaid ?? '—'}</td>
                    <td>{['Confirmed', 'FailedOnChain'].includes(stage.state) && ['Source', 'Target'].includes(stage.phase) ? (
                      <Button size="sm" variant="ghost" disabled={busy} onClick={() => void run(() => azoa.api.quoteWttCleanup(operation.operationId, stage.stageId), setCleanup)}>Residual funds</Button>
                    ) : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="flex flex-wrap gap-2">
              {operation.nextAction === 'Continue' ? (
                <Button size="sm" disabled={busy} onClick={() => void run(() => azoa.api.continueWtt(operation.operationId, { expectedVersion: operation.version, authorizeSubmission: true }), setOperation)}>Sign and send next stage</Button>
              ) : null}
              {operation.nextAction === 'Submit' ? (
                <Button size="sm" disabled={busy} onClick={() => { const value = command(); if (value) void run(() => azoa.api.submitWtt(operation.operationId, value), setOperation) }}>Send saved stage</Button>
              ) : null}
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => azoa.api.refreshWtt(operation.operationId), setOperation)}>Refresh</Button>
              {saved && operation.phase === 'Source' && !operation.transferSequence && !current?.submissionAuthorized && !current?.exposed && operation.state !== 'Cancelled' ? (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => azoa.api.cancelWtt({ offerId: saved.offerId, offerHash: saved.offerHash }, saved.requestKey), setOperation)}>Cancel before publication</Button>
              ) : null}
              {['Settled', 'CancelledBeforeEffect'].includes(operation.publicState) ? (
                <Button size="sm" variant="ghost" onClick={() => { persist(null); setOperation(null); setOffer(null) }}>Start another transfer</Button>
              ) : null}
            </div>

            {operation.nextAction === 'SignParticipant' && current ? (
              <div className="space-y-2 rounded-md border p-3">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={exposureConsent} onChange={(event) => setExposureConsent(event.target.checked)} />
                  Disclose the exact unsigned message for {current.participantAddress}. Once disclosed it can be submitted by anyone holding the signature and cannot be cancelled as unsent.
                </label>
                <Button size="sm" disabled={busy || !exposureConsent} onClick={() => { const value = command(); if (value) void run(() => azoa.api.exposeWttParticipant(operation.operationId, value), setParticipant) }}>Show message to sign</Button>
                {participant && participant.stageId === current.stageId ? (
                  <>
                    <p className="text-xs text-muted-foreground">Sign exactly this transaction with {participant.participantAddress}, unchanged.</p>
                    <div className="rounded-md bg-muted p-2 font-mono text-xs break-all">{participant.unsignedTransactionBase64}</div>
                    <Input placeholder="Signed transaction (base64)" value={signed} onChange={(event) => setSigned(event.target.value)} />
                    <Button size="sm" disabled={busy || !signed} onClick={() => void run(() => azoa.api.acceptWttParticipantSignature(operation.operationId, {
                      expectedVersion: participant.operationVersion, stageId: participant.stageId, expectedStageVersion: participant.stageVersion,
                      signedTransactionBase64: signed, authorizeSubmission: true,
                    }), (view) => { setOperation(view); setParticipant(null); setSigned('') })}>Submit signed transaction</Button>
                  </>
                ) : null}
              </div>
            ) : null}

            {operation.nextAction === 'StartNativeOutput' ? (
              <div className="space-y-2 rounded-md border p-3">
                <Button size="sm" disabled={busy} onClick={() => void run(() => azoa.api.quoteWttNativeOutput(operation.operationId), setNative)}>Price native delivery</Button>
                {native ? (
                  <>
                    <Row label="Native amount" value={native.nativeAmount} />
                    <Row label="Network fee" value={native.networkFee} />
                    <Row label="Temporary account rent (returned)" value={native.temporaryRent} />
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={nativeConsent} onChange={(event) => setNativeConsent(event.target.checked)} />The recipient accepts these costs and will sign the unwrap.</label>
                    <Button size="sm" disabled={busy || !nativeConsent || !saved} onClick={() => saved && void run(() => azoa.api.startWttNativeOutput(operation.operationId, {
                      expectedVersion: operation.version, requestKey: `${saved.requestKey}:native`, maximumNetworkFee: native.networkFee,
                      maximumTemporaryRent: native.temporaryRent, acceptNativeOutput: true,
                    }), (view) => { setOperation(view); setNative(null) })}>Prepare native delivery</Button>
                  </>
                ) : null}
              </div>
            ) : null}

            {cleanup ? (
              <div className="space-y-2 rounded-md border p-3">
                <Row label="Returned to" value={cleanup.destinationAddress} />
                <Row label="Amount returned" value={cleanup.returnAmount} />
                <Row label="Of which original deposit" value={cleanup.originalAttributedDepositReturn} />
                <Row label="Network fee" value={cleanup.networkFee} />
                <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={cleanupConsent} onChange={(event) => setCleanupConsent(event.target.checked)} />Return this exact balance to my original wallet and pay its fee.</label>
                <Button size="sm" disabled={busy || !cleanupConsent || !saved || cleanup.returnAmount === '0'} onClick={() => saved && void run(() => azoa.api.startWttCleanup(operation.operationId, {
                  expectedVersion: operation.version, stageId: cleanup.stageId, requestKey: `${saved.requestKey}:cleanup:${cleanup.stageId.slice(0, 8)}`,
                  maximumNetworkFee: cleanup.networkFee, acceptReturn: true,
                }), (view) => { setOperation(view); setCleanup(null); setCleanupConsent(false) })}>Return residual funds</Button>
              </div>
            ) : null}
          </div>
        ) : null}

        {error ? <p className="text-xs text-destructive">{error}</p> : null}
      </CardContent>
    </Card>
  )
}
