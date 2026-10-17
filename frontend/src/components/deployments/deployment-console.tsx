'use client'

import { useEffect, useRef, useState } from 'react'
import type { DeploymentArtifactView, DeploymentGenerateRequest, DeploymentReceipt, DeploymentStartRequest, NativeArtifact, NativeOperationIntent } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { DeploymentOperations } from './deployment-operations'

type SavedRequest = { key: string; request: DeploymentStartRequest }
const emptyGuid = '00000000-0000-0000-0000-000000000000'
const examples: Record<string, unknown> = {
  'avm-application/1': { approval: { kind: 'Teal', data: '#pragma version 8\nint 1' }, clearState: { kind: 'Teal', data: '#pragma version 8\nint 1' }, globalSchema: { integers: 0, bytes: 0 }, localSchema: { integers: 0, bytes: 0 }, extraPages: 0, argumentsBase64: [], accounts: [], foreignApplications: [], foreignAssets: [], boxes: [] },
  'evm-contract/1': { initCodeHex: '', expectedRuntimeCodeHex: '', constructorArguments: [] },
  'evm-call/1': { contractAddress: '', expectedCodeHash: '', method: '', arguments: [] },
  'solana-elf/1': { elfBase64: '', loader: 'loader-v3' },
  'solana-instruction/1': { programId: '', accounts: [], dataBase64: '', expectedProgramDataHash: '' },
  'solana-account/1': { operation: 'CreateAta', mint: '', owner: '' },
}

export function DeploymentConsole({ bundleId }: { bundleId: string }) {
  const { userId } = useAzoa()
  return userId ? <DeploymentEditor key={`${userId}:${bundleId}`} userId={userId} bundleId={bundleId} /> : null
}

function DeploymentEditor({ userId, bundleId }: { userId: string; bundleId: string }) {
  const { wallets } = useAzoa()
  const [kind, setKind] = useState<'PlatformRuntime' | 'NativeContract'>('PlatformRuntime')
  const [chain, setChain] = useState('Algorand')
  const [network, setNetwork] = useState('Testnet')
  const [actions, setActions] = useState('')
  const [format, setFormat] = useState('avm-application/1')
  const [compiler, setCompiler] = useState('')
  const [content, setContent] = useState(JSON.stringify(examples['avm-application/1'], null, 2))
  const [operation, setOperation] = useState<'Deploy' | 'Call' | 'Account'>('Deploy')
  const [input, setInput] = useState('{}')
  const [walletId, setWalletId] = useState('')
  const [fee, setFee] = useState('0')
  const [rent, setRent] = useState('0')
  const [value, setValue] = useState('0')
  const [artifact, setArtifact] = useState<DeploymentArtifactView | null>(null)
  const [quoted, setQuoted] = useState<NativeOperationIntent | null>(null)
  const [key, setKey] = useState('')
  const [saved, setSaved] = useState<SavedRequest | null>(null)
  const [receipt, setReceipt] = useState<DeploymentReceipt | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const alive = useRef(true)
  const storageKey = `azoa:deployment:${userId}:${bundleId}`
  useEffect(() => {
    alive.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) { const parsed = JSON.parse(raw) as SavedRequest; if (!parsed.key || !parsed.request?.artifactId) throw new Error('Saved request is malformed.'); setSaved(parsed) }
      setReady(true)
    } catch { setError('The saved deployment request could not be read. Preserve it and reconcile server operations before starting another request.') }
    return () => { alive.current = false }
  }, [storageKey])
  const run = async (work: () => Promise<void>) => {
    setBusy(true); setError(null)
    try { await work() } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Request failed.') }
    finally { if (alive.current) setBusy(false) }
  }
  const record = (result: DeploymentReceipt) => {
    setReceipt(result)
    if (result.state === 'Confirmed' || result.state === 'Failed' || result.state === 'Cancelled') {
      localStorage.removeItem(storageKey); setSaved(null); setQuoted(null); setKey('')
    }
  }
  const generate = () => run(async () => {
    const request: DeploymentGenerateRequest = { target: { kind, chainType: chain, network } }
    if (kind === 'PlatformRuntime') request.actionIds = actions.split(',').map(item => item.trim()).filter(Boolean)
    else request.native = { format, compilerIdentity: compiler, content: JSON.parse(content) } as NativeArtifact
    const result = await azoa.api.generateDeployment(bundleId, request)
    if (!alive.current) return
    if (!isOk(result)) throw new Error(result.error.message)
    setArtifact(result.value); setQuoted(null); setKey(crypto.randomUUID()); setReceipt(null)
  })
  const quote = () => run(async () => {
    if (!artifact?.native) throw new Error('Generate the native artifact first.')
    const wallet = wallets.find(item => item.id === walletId && item.walletType === 'Platform' && item.chainType.toLowerCase() === artifact.target.chainType.toLowerCase())
    if (!wallet) throw new Error('Select a custody wallet on the artifact chain.')
    const details = JSON.parse(input) as Record<string, unknown>
    if (!details || typeof details !== 'object' || Array.isArray(details)) throw new Error('Operation settings must be a JSON object.')
    const scope = operation === 'Deploy' ? 'contract:deploy:sign' : operation === 'Call' ? 'contract:call:sign' : 'solana:account:sign'
    const intent: NativeOperationIntent = { version: 1, operationKey: '', kind: operation, chainType: artifact.target.chainType, network: artifact.target.network, protocolIdentity: '', artifactHash: artifact.artifactHash,
      input: { ...details, artifact: artifact.native, ...(artifact.target.chainType === 'Solana' ? { payer: wallet.address, authority: wallet.address } : { sender: wallet.address }) },
      authorities: [{ address: wallet.address, signingContext: { userId, walletId: wallet.id, isPlatform: false, actingTenantId: emptyGuid, grantorUserId: userId, scope, chainType: wallet.chainType } }],
      budget: { maximumNetworkFee: fee, maximumLockedRent: rent, maximumValue: value, quoteId: '' } }
    const result = await azoa.api.quoteDeployment({ artifactId: artifact.artifactId, requestKey: key, intent })
    if (!alive.current) return
    if (!isOk(result)) throw new Error(result.error.message)
    setQuoted(result.value)
  })
  const start = () => run(async () => {
    const entry = saved ?? (artifact ? { key, request: { artifactId: artifact.artifactId, artifactHash: artifact.artifactHash, target: artifact.target, nativeIntent: quoted } } : null)
    if (!entry || !entry.key || (entry.request.target.kind === 'NativeContract' && !entry.request.nativeIntent?.quote)) throw new Error('Review a current native quote before authorizing.')
    localStorage.setItem(storageKey, JSON.stringify(entry)); setSaved(entry)
    const result = await azoa.api.startDeployment(entry.request, entry.key)
    if (!alive.current) return
    if (!isOk(result)) throw new Error(result.error.message)
    record(result.value)
  })
  const recover = (cancel: boolean) => run(async () => {
    if (!saved) return
    const result = await (cancel ? azoa.api.cancelDeploymentRequest(saved.key, saved.request) : azoa.api.getDeploymentByRequest(saved.key))
    if (!alive.current) return
    if (!isOk(result)) throw new Error(result.error.message)
    record(result.value)
  })
  const locked = busy || !!saved || !ready
  return <section className="space-y-4 rounded-md border p-4">
    <h3 className="font-medium">Generate and deploy</h3>
    <p className="text-sm text-muted-foreground">Platform runtime registers an immutable executable workflow. Native targets deploy or call actual chain programs. Choose the target explicitly; changing it requires generation again.</p>
    <fieldset disabled={locked || !!artifact} className="grid gap-3 sm:grid-cols-3">
      <label>Target<select aria-label="Deployment target" className="block w-full rounded border p-2" value={kind} onChange={event => setKind(event.target.value as typeof kind)}><option value="PlatformRuntime">Platform runtime</option><option value="NativeContract">Native contract or account</option></select></label>
      <label>Chain<select aria-label="Deployment chain" className="block w-full rounded border p-2" value={chain} onChange={event => setChain(event.target.value)}>{['Algorand', 'Ethereum', 'Solana'].map(name => <option key={name}>{name}</option>)}</select></label>
      <label>Network<select aria-label="Deployment network" className="block w-full rounded border p-2" value={network} onChange={event => setNetwork(event.target.value)}>{['Devnet', 'Testnet', 'Mainnet'].map(name => <option key={name}>{name}</option>)}</select></label>
      {kind === 'PlatformRuntime' ? <label className="sm:col-span-3">Ordered published action IDs<Input value={actions} onChange={event => setActions(event.target.value)} placeholder="Comma-separated; attached ecosystem sources are also included" /></label> : <>
        <label>Artifact format<select aria-label="Artifact format" className="block w-full rounded border p-2" value={format} onChange={event => { setFormat(event.target.value); setContent(JSON.stringify(examples[event.target.value], null, 2)) }}>{Object.keys(examples).map(name => <option key={name}>{name}</option>)}</select></label>
        <label className="sm:col-span-2">Compiler and version<Input value={compiler} onChange={event => setCompiler(event.target.value)} placeholder="Exact compiler/toolchain identity" /></label>
        <label className="sm:col-span-3">Program artifact<Textarea aria-label="Program artifact" rows={9} value={content} onChange={event => setContent(event.target.value)} className="font-mono text-xs" /></label>
      </>}
    </fieldset>
    {!artifact && <Button disabled={locked} onClick={generate}>Generate immutable artifact</Button>}
    {artifact && <>
      <p className="break-all text-xs">Artifact {artifact.artifactHash} · {artifact.target.chainType} / {artifact.target.network}</p>
      {!saved && <Button variant="outline" disabled={busy} onClick={() => { setArtifact(null); setQuoted(null); setKey('') }}>Change target or artifact</Button>}
      {artifact.target.kind === 'NativeContract' && <fieldset disabled={locked || !!quoted} className="space-y-3">
        <label>Operation<select aria-label="Native operation" className="block w-full rounded border p-2" value={operation} onChange={event => setOperation(event.target.value as typeof operation)}><option>Deploy</option><option>Call</option><option>Account</option></select></label>
        <label>Signing wallet<select aria-label="Signing wallet" className="block w-full rounded border p-2" value={walletId} onChange={event => setWalletId(event.target.value)}><option value="">Select wallet</option>{wallets.filter(wallet => wallet.walletType === 'Platform' && wallet.chainType.toLowerCase() === artifact.target.chainType.toLowerCase()).map(wallet => <option key={wallet.id} value={wallet.id}>{wallet.label || wallet.address}</option>)}</select></label>
        <Label htmlFor={`native-input-${bundleId}`}>Operation settings</Label><Textarea id={`native-input-${bundleId}`} value={input} onChange={event => setInput(event.target.value)} rows={4} className="font-mono text-xs" />
        <p className="text-xs text-muted-foreground">AVM: operation Create or Call with applicationId. Solana: Create, Upgrade, Call, or Account; include programId and expectedProgramDataHash when required. EVM: valueBaseUnits. The exact compiled artifact and selected wallet are bound automatically.</p>
        <div className="grid gap-2 sm:grid-cols-3"><label>Maximum network fee<Input value={fee} onChange={event => setFee(event.target.value)} /></label><label>Maximum locked rent<Input value={rent} onChange={event => setRent(event.target.value)} /></label><label>Maximum transferred value<Input value={value} onChange={event => setValue(event.target.value)} /></label></div>
        <p className="text-xs">All amounts are integer native base units: wei (Ethereum), microAlgos (Algorand), or lamports (Solana). Caps cover the entire operation, including all stages.</p>
        <Button disabled={locked || !!quoted} onClick={quote}>Get authoritative quote</Button>
      </fieldset>}
      {quoted?.quote && <div className="space-y-2 rounded border p-3">
        <p>{quoted.chainType} / {quoted.network} · Quote expires {new Date(quoted.quote.expiresAt).toLocaleString()}</p>
        <p>Quoted network fee: {quoted.quote.networkFee}; locked rent: {quoted.quote.lockedRent}; transferred value: {quoted.quote.value} native base units.</p>
        <p>Authorized maximums: fee {quoted.budget.maximumNetworkFee}; rent {quoted.budget.maximumLockedRent}; value {quoted.budget.maximumValue}.</p>
        {quoted.quote.disclosure && <><p>{quoted.quote.disclosure.effect}</p>{quoted.quote.disclosure.refundDestination && <p>Return destination: {quoted.quote.disclosure.refundDestination}. Estimated return: {quoted.quote.disclosure.estimatedReturnedBaseUnits} base units; actual balance changes appear after confirmation.</p>}{quoted.quote.disclosure.resultingMetadata && <pre className="max-h-64 overflow-auto text-xs">{JSON.stringify(quoted.quote.disclosure.resultingMetadata, null, 2)}</pre>}</>}
        <p className="text-sm">Network fees may be spent even if a transaction fails. Later stages require explicit continuation.</p>
        {!saved && <Button variant="outline" disabled={busy} onClick={() => setQuoted(null)}>Revise limits or operation</Button>}
      </div>}
      <Button disabled={locked || (artifact.target.kind === 'NativeContract' && !quoted)} onClick={start}>{artifact.target.kind === 'PlatformRuntime' ? 'Activate runtime' : 'Authorize quoted operation'}</Button>
    </>}
    {saved && <div className="space-y-2"><p>A request is saved for recovery. Retry uses its original target, quote, and key.</p><div className="flex flex-wrap gap-2"><Button disabled={busy || !ready} onClick={start}>Retry saved request</Button><Button variant="outline" disabled={busy || !ready} onClick={() => recover(false)}>Find original request</Button><Button variant="outline" disabled={busy || !ready} onClick={() => recover(true)}>Cancel original request safely</Button></div><p className="text-xs">Cancellation fences delayed admission. Already submitted transactions must be reconciled before cancellation.</p></div>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
    <DeploymentOperations userId={userId} selected={receipt} onSelect={setReceipt} onTerminal={() => { if (saved) void recover(false) }} />
  </section>
}
