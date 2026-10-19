'use client'

import { useEffect, useId, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { Label } from '@/components/ui/label'
import { azoa, isOk } from '@/lib/azoa'
import { INTAKE_FIELDS, type DesignerAvailability, type DesignerDraft, type DesignerMessage, type DesignerReply, type WorkflowIntake } from './workflow-designer-types'

export function WorkflowDesigner({ onImport }: { onImport: (draft: DesignerDraft) => void }) {
  const fieldId = useId()
  const active = useRef(true)
  const [availability, setAvailability] = useState<DesignerAvailability | null>(null)
  const [intake, setIntake] = useState<WorkflowIntake>(() => Object.fromEntries(INTAKE_FIELDS.map(([key]) => [key, ''])) as WorkflowIntake)
  const [consent, setConsent] = useState(false)
  const [tenantId, setTenantId] = useState('')
  const [jurisdictionsConfirmed, setJurisdictionsConfirmed] = useState(false)
  const [economicTermsConfirmed, setEconomicTermsConfirmed] = useState(false)
  const [resourceHandles, setResourceHandles] = useState('')
  const [messages, setMessages] = useState<DesignerMessage[]>([])
  const [answer, setAnswer] = useState('')
  const [reply, setReply] = useState<DesignerReply | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reviewed, setReviewed] = useState(false)
  const [imported, setImported] = useState(false)

  useEffect(() => {
    active.current = true
    void azoa.api.request<DesignerAvailability>('GET', '/api/action-designer').then(result => {
      if (!active.current) return
      if (isOk(result)) setAvailability(result.value)
      else setError(result.error.status === 403 ? 'This assistant needs the DApp developer role and an active tenant account with a provisioning credential. Ask your DApp manager or node operator to complete tenant setup for your account.' : 'Unable to check assistant availability. You can still use the visual builder.')
    }).catch(() => { if (active.current) setError('Unable to check assistant availability. You can still use the visual builder.') })
    return () => { active.current = false }
  }, [])

  const interview = async () => {
    if (!consent || busy || !availability?.available || tenantId !== availability.tenantId) return
    const history: DesignerMessage[] = [...messages.slice(-14), ...(answer.trim() ? [{ role: 'user' as const, content: answer.trim() }] : [])]
    setBusy(true)
    setError(null)
    setReviewed(false)
    setImported(false)
    setReply(null)
    try {
      const result = await azoa.api.request<DesignerReply>('POST', '/api/action-designer/interview', {
        intake, messages: history, consentToProvider: true, tenantId, jurisdictionsConfirmed, economicTermsConfirmed,
        confirmedResourceHandles: resourceHandles.split(/[\n,]/).map(handle => handle.trim()).filter(Boolean),
      })
      if (!active.current) return
      if (isOk(result)) {
        setReply(result.value)
        const assistantMessage: DesignerMessage = { role: 'assistant', content: [result.value.summary, ...result.value.questions, ...result.value.concerns].join('\n').slice(0, 4000) }
        setMessages([...history, assistantMessage].slice(-16))
        setAnswer('')
      } else {
        setError(result.error.status === 429 ? 'The assistant request budget is used for now. Try later or continue in the visual builder.' : result.error.message)
      }
    } catch {
      if (active.current) setError('The assistant could not be reached. Your workflow has not been changed. Try again or use the visual builder.')
    } finally {
      if (active.current) setBusy(false)
    }
  }

  return (
    <details className="rounded-lg border p-4">
      <summary className="cursor-pointer font-semibold">Design an economic workflow with AI</summary>
      <div className="mt-4 space-y-4">
        <p className="text-sm text-muted-foreground">Explain what you are building. The assistant will challenge gaps, ask follow-up questions, and flag legal and tax questions before proposing a draft. It cannot approve compliance, save, publish or run your workflow.</p>
        {availability && <div className="rounded border bg-muted/30 p-3 text-sm">
          <p className="font-medium">AI data sharing: {availability.provider} · {availability.model}</p>
          <p className="mt-1">{availability.disclosure}</p>
          <p className="mt-1">The latest eight interview turns are retained in this browser view. Closing the page or changing account discards the interview.</p>
          <div className="mt-3 space-y-1">
            <Label htmlFor={`${fieldId}-tenant`}>Tenant context</Label>
            <select id={`${fieldId}-tenant`} className="block w-full rounded border bg-background p-2" value={tenantId} disabled={busy}
              onChange={event => { setTenantId(event.target.value); setReply(null); setReviewed(false) }}>
              <option value="">Select your verified tenant account</option>
              <option value={availability.tenantId}>Your tenant · {availability.tenantId}</option>
            </select>
            <p>The server verifies this context belongs to your account and still has active tenant authority on every request. Keys for this tenant share one AI request budget.</p>
          </div>
          {availability.available ? <label className="mt-3 flex items-start gap-2">
            <input type="checkbox" checked={consent} disabled={busy} onChange={event => { setConsent(event.target.checked); setReviewed(false) }} />
            <span>I am authorized to share this brief and agree to send it and the interview to these AI providers.</span>
          </label> : <p className="mt-2 font-medium">The node operator has not enabled the AI assistant. The visual builder is available below.</p>}
        </div>}

        <div className="grid gap-4 lg:grid-cols-2">
          {INTAKE_FIELDS.map(([key, label, hint]) => <div key={key} className="space-y-1.5">
            <Label htmlFor={`${fieldId}-${key}`}>{label}</Label>
            <p id={`${fieldId}-${key}-hint`} className="text-xs text-muted-foreground">{hint}</p>
            <Textarea id={`${fieldId}-${key}`} aria-describedby={`${fieldId}-${key}-hint`} value={intake[key]} maxLength={2500} disabled={busy}
              onChange={event => { setIntake(current => ({ ...current, [key]: event.target.value })); setReply(null); setReviewed(false); setImported(false); setJurisdictionsConfirmed(false); setEconomicTermsConfirmed(false) }} />
          </div>)}
        </div>

        <div className="space-y-2 rounded border p-3 text-sm">
          <p>Leave these unchecked while you are exploring. A draft requires confirmed terms; the assistant can keep interviewing you first.</p>
          <label className="flex items-start gap-2"><input type="checkbox" checked={jurisdictionsConfirmed} disabled={busy} onChange={event => { setJurisdictionsConfirmed(event.target.checked); setReply(null); setReviewed(false) }} /><span>I have identified the relevant countries/states and tax residences in this brief.</span></label>
          <label className="flex items-start gap-2"><input type="checkbox" checked={economicTermsConfirmed} disabled={busy} onChange={event => { setEconomicTermsConfirmed(event.target.checked); setReply(null); setReviewed(false) }} /><span>I have specified the economic terms, exact units, allocations, approvals, refunds and failure paths.</span></label>
          <Label htmlFor={`${fieldId}-resources`}>Confirmed workflow resource identifiers (optional)</Label>
          <Textarea id={`${fieldId}-resources`} value={resourceHandles} disabled={busy} maxLength={8000} aria-describedby={`${fieldId}-resource-hint`}
            onChange={event => { setResourceHandles(event.target.value); setReply(null); setReviewed(false) }} />
          <p id={`${fieldId}-resource-hint`} className="text-xs text-muted-foreground">One exact resource identifier per line: existing wallet, hold, asset, recipient, reserve partition, currency code or quote identifiers you verified in your own tenant. Do not add keys, signatures or personal records. Only provide identifiers for resources you own. Normal ownership and provider-validity checks still apply when you later run an action.</p>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor={`${fieldId}-answer`}>Answer follow-up questions or challenge an assumption</Label>
          <Textarea id={`${fieldId}-answer`} value={answer} maxLength={4000} disabled={busy}
            onChange={event => { setAnswer(event.target.value); setReviewed(false) }} placeholder="You can say what is unknown. Update the brief above as your decisions become clearer." />
        </div>
        <Button type="button" disabled={!consent || !availability?.available || tenantId !== availability.tenantId || busy || (!intake.purpose.trim() && !answer.trim())} onClick={() => void interview()}>
          {busy ? 'Reviewing your brief…' : messages.length ? 'Continue the interview' : 'Ask the assistant'}
        </Button>
        <div role="status" aria-live="polite">{busy && <p className="text-sm">Checking the economic terms and preparing questions. This can take up to 90 seconds.</p>}</div>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}

        {reply && <section aria-label="Assistant review" className="space-y-3 rounded border p-3">
          <p className="whitespace-pre-wrap text-sm">{reply.summary}</p>
          {([
            ['Questions to resolve', reply.questions], ['Legal, tax and operational concerns', reply.concerns],
            ['Assumptions to confirm', reply.assumptions], ['Draft validation issues', reply.draftErrors],
          ] as const).map(([title, items]) => items.length > 0 && <div key={title}>
            <h4 className="text-sm font-semibold">{title}</h4>
            <ul className="ml-5 mt-1 list-disc space-y-1 text-sm">{items.map((item, index) => <li key={index} className="whitespace-pre-wrap">{item}</li>)}</ul>
          </div>)}
          <div className="space-y-1 text-xs text-muted-foreground">
            <p className="font-semibold">Official reference starting points · US examples, reviewed 18 October 2026</p>
            <p>These sources do not determine your classification or cover other countries. Confirm local legal and tax requirements with qualified advisers before launch.</p>
            {reply.sources.map(source => <p key={source.id}><a href={source.url} target="_blank" rel="noopener noreferrer" className="underline">{source.title}</a> — {source.jurisdiction}</p>)}
          </div>
          {reply.draft && reply.readyForDraft && <div className="space-y-3 border-t pt-3">
            <h4 className="font-semibold">Proposed private draft: {reply.draft.name}</h4>
            <p className="text-sm">{reply.draft.description}</p>
            <ol className="ml-5 list-decimal text-sm">{reply.draft.nodes.map((node, index) => <li key={index}>{node.name} ({node.nodeType})</li>)}</ol>
            <details><summary className="cursor-pointer text-sm">Review exact node configurations and connections</summary><pre className="max-h-80 overflow-auto whitespace-pre-wrap rounded bg-muted p-2 text-xs">{JSON.stringify(reply.draft, null, 2)}</pre></details>
            <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={reviewed} onChange={event => setReviewed(event.target.checked)} /><span>I reviewed the proposed amounts, recipients, approvals, refunds and legal/tax questions. Importing replaces the current canvas; I will review it there before saving a private draft.</span></label>
            <Button type="button" disabled={!reviewed || !consent || !jurisdictionsConfirmed || !economicTermsConfirmed || tenantId !== availability?.tenantId || busy || !!answer.trim() || imported} onClick={() => { if (reply.draft) { onImport(reply.draft); setImported(true) } }}>Import into visual builder</Button>
            {imported && <p role="status" className="text-sm">Draft imported below. Review every node, then use Create Action to save. Nothing has been published or run.</p>}
          </div>}
        </section>}
      </div>
    </details>
  )
}
