import { describe, expect, it } from 'vitest'
import { blocksText, indexBodyText, WORKFLOW_INDEX, workflowBodyText } from './workflows'
import {
  absoluteUrl,
  buildFeedXml,
  buildLlmsTxt,
  ESSAYS,
  injectsRuntimeApi,
  jsonLdGraph,
  organizationJsonLd,
  publicDocuments,
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
    for (const path of ['/', '/architecture', '/federation', '/privacy', '/terms', '/contact', '/blog', '/notes', '/workflows']) {
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
    expect(injectsRuntimeApi('/contact')).toBe(false)
    expect(injectsRuntimeApi('/workflows/settle-once')).toBe(false)
    expect(injectsRuntimeApi('/workflows/reconciliation-over-blind-retry')).toBe(false)
    expect(injectsRuntimeApi('/login')).toBe(true)
    expect(injectsRuntimeApi('/overview')).toBe(true)
    expect(injectsRuntimeApi('/no-such-page')).toBe(false)
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

  it('keeps public titles unique and within the search-title length', () => {
    const documents = publicDocuments()
    const titles = documents.map((document) => document.title)
    const descriptions = documents.map((document) => document.description)
    expect(new Set(titles).size).toBe(titles.length)
    expect(new Set(descriptions).size).toBe(descriptions.length)
    const home = documents.find((document) => document.path === '/')
    expect(home?.title).toBe('AZOA — Autonomous Zones of Action')
    for (const document of documents) {
      expect(document.title.length).toBeLessThanOrEqual(60)
      if (document.path !== '/') expect(document.title.endsWith('AZOA')).toBe(true)
      expect(document.title === home?.title).toBe(document.path === '/')
      expect(document.description.length).toBeGreaterThan(80)
      expect(absoluteUrl(document.path).startsWith('https://azoa.ardanova.com')).toBe(true)
    }
    const contact = documents.find((document) => document.path === '/contact')
    expect(contact?.description).toContain('contact@escherbridge.com')
    expect(contact?.description).toContain('Boise, Idaho')
    expect(contact?.index).toBe(true)
  })

  it('lists real public urls in llms.txt and keeps the operator at city level', () => {
    const text = buildLlmsTxt()
    expect(text.startsWith('# AZOA')).toBe(true)
    expect(text).toContain('Escherbridge, LLC, Boise, Idaho')
    expect(text).toContain('contact@escherbridge.com')
    expect(text).toContain(SIMULATION)
    for (const document of sitemapDocuments()) {
      expect(text).toContain(absoluteUrl(document.path))
    }
    expect(text).toContain(absoluteUrl('/feed.xml'))
    expect(text).toContain(absoluteUrl('/sitemap.xml'))
    expect(text).toContain(absoluteUrl('/news'))
    expect(text).not.toContain(absoluteUrl('/login'))
    expect(text).not.toContain(absoluteUrl('/operator'))
    expect(text).not.toContain(absoluteUrl('/api'))
    expect(text).not.toContain('railway.app')
    expect(text).not.toContain('streetAddress')
    expect(text).not.toMatch(/\b\d{1,6}\s+[A-Za-z0-9.'#-]+\s+(?:Street|St|Avenue|Ave|Road|Rd|Drive|Dr)\b/)
  })

  it('puts a homepage collection list on the public sections', () => {
    const home = JSON.stringify(jsonLdGraph('/'))
    expect(home).toContain('"@type":"CollectionPage"')
    expect(home).toContain('"@type":"ItemList"')
    expect(home).toContain(absoluteUrl('/contact'))
    expect(home).toContain(absoluteUrl('/workflows'))
    expect(home).not.toContain(absoluteUrl('/news'))
    expect(home).not.toContain(absoluteUrl('/login'))
    const contact = JSON.stringify(jsonLdGraph('/contact'))
    expect(contact).toContain('"@type":"BreadcrumbList"')
    expect(contact).toContain('"name":"Contact","item":"https://azoa.ardanova.com/contact"')
    expect(contact).toContain('contact@escherbridge.com')
    expect(contact).not.toContain('streetAddress')
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
      const document = publicDocuments().find((item) => item.path === `/workflows/${workflow.slug}`)
      expect(document?.modifiedAt).toBe(workflow.lastReviewed)
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
