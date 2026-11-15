import { describe, expect, it } from 'vitest'
import { blocksText, indexBodyText, WORKFLOW_INDEX, workflowBodyText } from './workflows'
import {
  absoluteUrl,
  buildFeedXml,
  ESSAYS,
  injectsRuntimeApi,
  jsonLdGraph,
  organizationJsonLd,
  SIMULATION,
  sitemapDocuments,
  WORKFLOWS,
} from './public-documents'

const EXPECTED_SLUGS = [
  'settle-once',
  'reconciliation-over-blind-retry',
  'fail-closed',
  'version-pinned-execution',
  'durable-waits-and-signals',
  'consent-records',
  'idempotent-requests',
  'consent-withdrawal',
  'least-privilege',
  'separation-of-duties',
  'event-logging',
  'workflow-state-machines',
  'compensating-transactions',
  'safe-and-idempotent',
  'demonstrable-consent',
  'distinguishable-consent',
]

describe('public documents', () => {
  it('publishes marketing, notes, blog, and all 16 workflow urls on the custom host', () => {
    const urls = sitemapDocuments().map((document) => absoluteUrl(document.path))
    for (const path of ['/', '/architecture', '/federation', '/privacy', '/terms', '/blog', '/notes', '/workflows']) {
      expect(urls).toContain(absoluteUrl(path))
    }
    for (const essay of ESSAYS) expect(urls).toContain(absoluteUrl(`/notes/${essay.slug}`))
    for (const workflow of WORKFLOWS) expect(urls).toContain(absoluteUrl(`/workflows/${workflow.slug}`))
    expect(urls).not.toContain(absoluteUrl('/news'))
    expect(urls).not.toContain(absoluteUrl('/login'))
    expect(urls.join('\n')).not.toContain('railway.app')
    expect(urls.join('\n')).not.toContain('azoa.io')
    expect(urls.join('\n')).not.toContain('/guides')
  })

  it('builds an RSS feed from the essays and workflow pages', () => {
    const xml = buildFeedXml()
    expect(xml.startsWith('<?xml')).toBe(true)
    expect(xml).toContain('<rss version="2.0">')
    expect(xml).toContain('/notes/settle-once-or-not-at-all')
    for (const slug of EXPECTED_SLUGS) {
      expect(xml).toContain(`/workflows/${slug}`)
    }
    expect(xml).not.toContain('/workflows/action-graphs')
    expect(xml).not.toContain('/workflows/grants-and-bounties')
    expect(xml).not.toContain('railway.app')
    expect(xml).not.toContain('never double-spend')
    expect(xml.toLowerCase()).not.toContain('kyc is available')
    expect(xml).not.toContain('FIAT-BRIDGED')
  })

  it('keeps the runtime API url off public html and on the sign-in route', () => {
    expect(injectsRuntimeApi('/')).toBe(false)
    expect(injectsRuntimeApi('/architecture')).toBe(false)
    expect(injectsRuntimeApi('/workflows/settle-once')).toBe(false)
    expect(injectsRuntimeApi('/workflows/reconciliation-over-blind-retry')).toBe(false)
    expect(injectsRuntimeApi('/login')).toBe(true)
    expect(injectsRuntimeApi(null)).toBe(true)
  })

  it('publishes a city-level operator address and honest simulation constant', () => {
    expect(organizationJsonLd().parentOrganization.address).toEqual({
      '@type': 'PostalAddress',
      addressLocality: 'Boise',
      addressRegion: 'ID',
      addressCountry: 'US',
    })
    expect(SIMULATION).toContain('simulated payloads')
    expect(SIMULATION).toContain('fiat settlement')
  })

  it('organizes workflows by guarantee with practitioner copy, bottom citations, and clean safety claims', () => {
    expect(ESSAYS).toHaveLength(2)
    expect(WORKFLOWS.map((workflow) => workflow.slug)).toEqual(EXPECTED_SLUGS)
    const index = indexBodyText()
    expect(index).toContain(WORKFLOW_INDEX.h1)
    expect(blocksText(WORKFLOW_INDEX.blocks)).toContain('Idempotency & Sagas')
    expect(blocksText(WORKFLOW_INDEX.blocks)).toContain('Consent & Access Control')
    expect(blocksText(WORKFLOW_INDEX.blocks)).toContain('Audit & Fail-Closed Guards')
    expect(index).toContain('https://ardanova.com')
    expect(index).toContain('https://primusneo.com')
    for (const slug of EXPECTED_SLUGS) {
      expect(index).toContain(`/workflows/${slug}`)
    }
    expect(index).not.toContain('azoa.io')
    expect(index).not.toContain('/patterns')

    for (const workflow of WORKFLOWS) {
      const text = workflowBodyText(workflow)
      expect(text).toContain(workflow.h1)
      expect(blocksText(workflow.blocks, workflow.sources)).toContain('Sources')
      expect(text).toContain('In practice')
      expect(text).not.toContain('This public site is a simulation.')
      expect(text).not.toContain('research pass (403)')
      expect(text).not.toContain('HTTP 500 / failed fetch')
      expect(text).not.toContain('azoa.io')
      expect(text).not.toContain('ardanova.com/patterns')
      expect(text).not.toContain('never double-spend')
      expect(text).not.toContain('FIAT-BRIDGED')
      expect(text.toLowerCase()).not.toContain('kyc is available')
      expect(text.toLowerCase()).not.toContain('live custody is')
      for (const source of workflow.sources) {
        for (const url of source.urls) expect(text).toContain(url)
      }
    }

    const bySlug = Object.fromEntries(WORKFLOWS.map((workflow) => [workflow.slug, workflow]))
    const settle = workflowBodyText(bySlug['settle-once']!)
    expect(settle).toContain('Stripe API v1')
    expect(settle).toContain('admi.002')
    expect(settle).toContain('pacs.028')
    expect(settle).toContain('Version 3.2 (June 2025)')

    const reconcile = workflowBodyText(bySlug['reconciliation-over-blind-retry']!)
    expect(reconcile).toContain('AwaitingReconciliation')
    expect(reconcile).toContain('WorkflowClient.reconcileRun')

    const pinned = workflowBodyText(bySlug['version-pinned-execution']!)
    expect(pinned).toContain('expectedPublishedHash')
    expect(pinned).toContain('ActionEconomicManifest')

    const durable = workflowBodyText(bySlug['durable-waits-and-signals']!)
    expect(durable).toContain('AwaitingSignal')
    expect(durable).toContain('AwaitingTimer')
    expect(durable).toContain('stale thread')

    const safe = workflowBodyText(bySlug['safe-and-idempotent']!)
    expect(safe).toContain('page?do=delete')
    expect(safe).toContain('https://www.rfc-editor.org/rfc/rfc9110.html')
  })

  it('puts article, breadcrumb, and collection JSON-LD on the workflow routes', () => {
    const index = JSON.stringify(jsonLdGraph('/workflows'))
    expect(index).toContain('"@type":"CollectionPage"')
    expect(index).toContain('"@type":"ItemList"')
    expect(index).toContain('"@type":"BreadcrumbList"')
    expect(index).toContain('"name":"Workflows"')
    expect(index).toContain('"name":"Escherbridge"')
    expect(index).toContain('Escherbridge, LLC')
    for (const workflow of WORKFLOWS) {
      expect(index).toContain(absoluteUrl(`/workflows/${workflow.slug}`))
      const article = JSON.stringify(jsonLdGraph(`/workflows/${workflow.slug}`))
      expect(article).toContain('"@type":"Article"')
      expect(article).toContain('"@type":"BreadcrumbList"')
      expect(article).toContain(`"name":"${workflow.title}"`)
      expect(article).toContain(workflow.h1)
      expect(article).toContain(`"mainEntityOfPage":"${absoluteUrl(`/workflows/${workflow.slug}`)}"`)
      expect(article).toContain('Escherbridge, LLC')
      for (const source of workflow.sources) {
        expect(article).toContain(source.name)
        for (const url of source.urls) expect(article).toContain(url)
      }
    }
    expect(index).not.toContain('action-graphs')
    expect(index).not.toContain('grants-and-bounties')
  })
})
