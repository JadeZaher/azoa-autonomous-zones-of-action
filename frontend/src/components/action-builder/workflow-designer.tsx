'use client'

import { useEffect, useId, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { Label } from '@/components/ui/label'
import { hasWorkflowPlanningDraft, type WorkflowDesignerRequest } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { INTAKE_FIELDS, type DesignerAvailability, type DesignerDraft, type DesignerMessage, type DesignerReply, type WorkflowIntake } from './workflow-designer-types'

export function WorkflowDesigner({ onImport }: { onImport: (draft: DesignerDraft) => void }) {
  const fieldId = useId()
  const active = useRef(true)
  const [availability, setAvailability] = useState<DesignerAvailability | null>(null)
  const [intake, setIntake] = useState<WorkflowIntake>({
    purpose: '', participants: '', jurisdictions: '', valueMovement: '', allocationsAndTax: '', failureAndRefunds: '',
  })
  const [consent, setConsent] = useState(false)
  const [tenantId, setTenantId] = useState('')
  const [jurisdictionsConfirmed, setJurisdictionsConfirmed] = useState(false)
  const [economicTermsConfirmed, setEconomicTermsConfirmed] = useState(false)
  const [planningScope, setPlanningScope] = useState(false)
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
      const payload: WorkflowDesignerRequest = {
        intake, messages: history, consentToProvider: true, tenantId, jurisdictionsConfirmed, economicTermsConfirmed,
        planningScopeAccepted: planningScope,
      }
      const result = await azoa.api.request<DesignerReply>('POST', '/api/action-designer/interview', payload)
      if (!active.current) return
      if (isOk(result)) {
        setReply(result.value)
        const assistantMessage: DesignerMessage = { role: 'assistant', content: [result.value.summary, ...result.value.questions, ...result.value.reviewQuestions, ...result.value.concerns, ...result.value.limitations].join('\n').slice(0, 4000) }
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
        <p className="text-sm text-muted-foreground">Explain what you are building. The assistant will challenge operational gaps and provide a legal/tax review checklist. You may choose an editable planning/data-only graph; it cannot move funds, enforce business controls, approve compliance, save, publish or run your workflow.</p>
        {availability && <div className="rounded border bg-muted/30 p-3 text-sm">
          <p className="font-medium">AI data sharing: {availability.provider} · {availability.model}</p>
          <p className="mt-1">{availability.disclosure}</p>
          <p className="mt-1">The latest eight interview turns are retained in this browser view. Closing the page or changing account discards the interview.</p>
          <div className="mt-3 space-y-1">
            <Label htmlFor={`${fieldId}-tenant`}>Tenant context</Label>
            <select id={`${fieldId}-tenant`} className="block w-full rounded border bg-background p-2" value={tenantId} disabled={busy}
              onChange={event => { setTenantId(event.target.value); setReply(null); setReviewed(false) }}>
              <option value="">Select your verified tenant account</option>
              {availability.tenantId && <option value={availability.tenantId}>Your tenant · {availability.tenantId}</option>}
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
          <fieldset className="space-y-2" disabled={busy}>
            <legend className="font-semibold">Choose the assistant scope</legend>
            <label className="flex items-start gap-2"><input type="radio" name={`${fieldId}-scope`} checked={!planningScope} onChange={() => { setPlanningScope(false); setReply(null); setReviewed(false); setImported(false) }} /><span>Continue the interview only; I can use the manual builder separately.</span></label>
            <label className="flex items-start gap-2"><input type="radio" name={`${fieldId}-scope`} checked={planningScope} onChange={() => { setPlanningScope(true); setReply(null); setReviewed(false); setImported(false) }} /><span>I want a planning/data-only graph that records manual/external handoff notes. It does not implement two-person approval, eligibility, caps, refunds, payouts or settlement, and moves no funds. Importing does not execute it. A later explicit run can enqueue webhook notifications to a configured tenant registration and requires an active registration; checkpoints neither wait for nor prove review or approval.</span></label>
          </fieldset>
          <p>Leave facts unconfirmed while exploring. Adviser-review questions do not block a planning draft, but qualified legal/tax determinations are needed before real operation.</p>
          <label className="flex items-start gap-2"><input type="checkbox" checked={jurisdictionsConfirmed} disabled={busy} onChange={event => { setJurisdictionsConfirmed(event.target.checked); setReply(null); setReviewed(false) }} /><span>I have identified the relevant countries/states and tax residences in this brief.</span></label>
          <label className="flex items-start gap-2"><input type="checkbox" checked={economicTermsConfirmed} disabled={busy} onChange={event => { setEconomicTermsConfirmed(event.target.checked); setReply(null); setReviewed(false) }} /><span>I have specified the economic terms, exact units, allocations, approvals, refunds and failure paths.</span></label>
          <p>No wallet, payment or identity resource identifiers are needed for planning checkpoints. Do not enter keys or personal records.</p>
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
          <p role="status" className="whitespace-pre-wrap text-sm">{reply.summary}</p>
          {([
            ['Operational details needed for planning', reply.questions], ['Legal, tax and operational review checklist', reply.concerns],
            ['Questions for qualified advisers — not planning blockers', reply.reviewQuestions], ['Planning scope and unsupported capabilities', reply.limitations],
            ['Facts and assumptions requiring review', reply.assumptions], ['Draft validation issues', reply.draftErrors],
          ] as const).map(([title, items]) => items.length > 0 && <div key={title}>
            <h4 className="text-sm font-semibold">{title}</h4>
            <ul className="ml-5 mt-1 list-disc space-y-1 text-sm">{items.map((item, index) => <li key={index} className="whitespace-pre-wrap">{item}</li>)}</ul>
          </div>)}
          <div className="space-y-1 text-xs text-muted-foreground">
            <p className="font-semibold">Official reading guides · US examples only</p>
            <p>These are starting points, not citations establishing a conclusion about your workflow. They do not determine your classification or cover other countries. Confirm current local legal and tax requirements with qualified advisers before operation.</p>
            {reply.sources.map(source => <p key={source.id}><a href={source.url} target="_blank" rel="noopener noreferrer" className="underline">{source.title}</a> — {source.jurisdiction}</p>)}
          </div>
          {planningScope && hasWorkflowPlanningDraft(reply) && <div className="space-y-3 border-t pt-3">
            <h4 className="font-semibold">Proposed private planning draft: {reply.draft.name}</h4>
            <p className="text-sm">{reply.draft.description}</p>
            <p className="text-sm">This is an editable starting point for visual authoring, not an automated economic workflow. Importing does not execute it. A later explicit run of these Emit nodes can enqueue webhook notifications to a configured tenant registration and requires an active registration. Checkpoints neither wait for nor prove review or approval. Use the full manual builder and separately reviewed systems to implement actual controls and settlement.</p>
            <ol className="ml-5 list-decimal text-sm">{reply.draft.nodes.map((node, index) => <li key={index}>{node.name} ({node.nodeType})</li>)}</ol>
            <details><summary className="cursor-pointer text-sm">Review exact node configurations and connections</summary><pre className="max-h-80 overflow-auto whitespace-pre-wrap rounded bg-muted p-2 text-xs">{JSON.stringify(reply.draft, null, 2)}</pre></details>
            <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={reviewed} onChange={event => setReviewed(event.target.checked)} /><span>I reviewed the planning handoff notes. This graph does not implement two-person approval, eligibility, caps, refunds, payouts or settlement and does not approve compliance. Importing replaces the canvas without execution. I understand a later explicit run can enqueue tenant webhook notifications and requires an active registration; checkpoints do not wait for or prove review or approval. I will review it before saving a private draft.</span></label>
            <Button type="button" disabled={!reviewed || !planningScope || !consent || !jurisdictionsConfirmed || !economicTermsConfirmed || tenantId !== availability?.tenantId || busy || !!answer.trim() || imported} onClick={() => { if (planningScope && hasWorkflowPlanningDraft(reply)) { onImport(reply.draft); setImported(true) } }}>Import into visual builder</Button>
            {imported && <p role="status" className="text-sm">Planning draft imported below. Review every checkpoint before saving. Nothing has moved funds, settled, been published or run.</p>}
          </div>}
        </section>}
      </div>
    </details>
  )
}
