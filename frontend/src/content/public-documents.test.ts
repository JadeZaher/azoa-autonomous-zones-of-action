import { describe, expect, it } from 'vitest'
import { blocksText, indexBodyText, SIMULATION_SENTENCE, WORKFLOW_INDEX, workflowBodyText } from './workflows'
import {
  absoluteUrl,
  buildFeedXml,
  ESSAYS,
  injectsRuntimeApi,
  jsonLdGraph,
  sitemapDocuments,
  WORKFLOWS,
} from './public-documents'

describe('public documents', () => {
  it('publishes marketing, notes, blog, and workflow urls on the custom host', () => {
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
  })

  it('builds an RSS feed from the essays and workflow pages', () => {
    const xml = buildFeedXml()
    expect(xml.startsWith('<?xml')).toBe(true)
    expect(xml).toContain('<rss version="2.0">')
    expect(xml).toContain('/notes/settle-once-or-not-at-all')
    expect(xml).toContain('/workflows/consent-records')
    expect(xml).toContain('/workflows/settle-once')
    expect(xml).toContain('/workflows/fail-closed')
    expect(xml).not.toContain('/workflows/action-graphs')
    expect(xml).not.toContain('/workflows/grants-and-bounties')
    expect(xml).not.toContain('railway.app')
    expect(xml).not.toContain('never double-spend')
    expect(xml).toContain('not a KYC feature')
    expect(xml.toLowerCase()).not.toContain('kyc is available')
    expect(xml).not.toContain('FIAT-BRIDGED')
  })

  it('keeps the runtime API url off public html and on the sign-in route', () => {
    expect(injectsRuntimeApi('/')).toBe(false)
    expect(injectsRuntimeApi('/architecture')).toBe(false)
    expect(injectsRuntimeApi('/workflows/settle-once')).toBe(false)
    expect(injectsRuntimeApi('/login')).toBe(true)
    expect(injectsRuntimeApi(null)).toBe(true)
  })

  it('states the simulation on the index and every workflow, with the cited sources', () => {
    expect(ESSAYS).toHaveLength(2)
    expect(WORKFLOWS.map((workflow) => workflow.slug)).toEqual(['settle-once', 'fail-closed', 'consent-records'])
    const index = indexBodyText()
    expect(index).toContain('Shapes the engine can describe.')
    expect(index).toContain(SIMULATION_SENTENCE)
    expect(blocksText(WORKFLOW_INDEX.blocks)).toContain(SIMULATION_SENTENCE)
    expect(index).toContain('https://ardanova.com')
    expect(index).toContain('/notes/settle-once-or-not-at-all')
    expect(index).toContain('/notes/fail-closed-or-it-isnt-a-control')
    expect(index).not.toContain('azoa.io')
    expect(index).not.toContain('/patterns')

    for (const workflow of WORKFLOWS) {
      const text = workflowBodyText(workflow)
      expect(text).toContain(workflow.h1)
      expect(text).toContain(SIMULATION_SENTENCE)
      expect(blocksText(workflow.blocks, workflow.sources)).toContain(SIMULATION_SENTENCE)
      expect(text).toContain('Sources')
      expect(text).toContain('An illustrative scenario')
      expect(text).toContain('https://ardanova.com')
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

    const settle = workflowBodyText(WORKFLOWS[0]!)
    expect(settle).toContain('including 500 errors')
    expect(settle).toContain('retries don’t repeat successful operations')
    expect(settle).toContain('admi.002')
    expect(settle).toContain('pacs.028')
    expect(settle).toContain('Version 3.2 (June 2025)')

    const closed = workflowBodyText(WORKFLOWS[1]!)
    expect(closed).toContain('Base access decisions on permission rather than exclusion.')
    expect(closed).toContain('11 October 1974')
    expect(closed).toContain('17 April 1975')
    expect(closed).toContain('E. Glaser in 1965')
    expect(closed).toContain('Every access to every object must be checked for authority')

    const consent = workflowBodyText(WORKFLOWS[2]!)
    expect(consent).toContain('as easy to withdraw as to give consent')
    expect(consent).toContain('clearly distinguishable from the other matters')
    expect(consent).toContain('utmost account shall be taken')
    expect(consent).toContain('reasoned-objection check in ACT')
    expect(consent).toContain('https://azoa.ardanova.com/privacy')
    expect(consent).toContain('could not be retrieved in the research pass (403)')
    expect(consent).toContain('does not paraphrase ICO audit-trail field lists from memory')
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
      expect(article).toContain('"datePublished":"2026-11-10"')
      expect(article).toContain('"dateModified":"2026-11-10"')
      expect(article).toContain(`"mainEntityOfPage":"${absoluteUrl(`/workflows/${workflow.slug}`)}"`)
      expect(article).toContain('Escherbridge, LLC')
      for (const source of workflow.sources) {
        expect(article).toContain(source.name)
        for (const url of source.urls) expect(article).toContain(url)
      }
    }
    const consentLd = JSON.stringify(jsonLdGraph('/workflows/consent-records'))
    expect(consentLd).not.toContain('/privacy')
    expect(index).not.toContain('action-graphs')
    expect(index).not.toContain('grants-and-bounties')
  })
})
