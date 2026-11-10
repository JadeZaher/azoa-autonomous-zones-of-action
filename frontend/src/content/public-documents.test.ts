import { describe, expect, it } from 'vitest'
import {
  absoluteUrl,
  buildFeedXml,
  ESSAYS,
  injectsRuntimeApi,
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
    expect(xml).not.toContain('railway.app')
    expect(xml).not.toContain('never double-spend')
    expect(xml.toLowerCase()).not.toContain('kyc')
  })

  it('keeps the runtime API url off public html and on the sign-in route', () => {
    expect(injectsRuntimeApi('/')).toBe(false)
    expect(injectsRuntimeApi('/architecture')).toBe(false)
    expect(injectsRuntimeApi('/workflows/settle-once')).toBe(false)
    expect(injectsRuntimeApi('/login')).toBe(true)
    expect(injectsRuntimeApi(null)).toBe(true)
  })

  it('states the simulation on every workflow and does not invent a news item', () => {
    expect(ESSAYS).toHaveLength(2)
    for (const workflow of WORKFLOWS) {
      const text = workflow.paragraphs.join(' ')
      expect(text).toContain('This hosted site is a simulation')
      expect(text).toContain('does not move value')
      expect(text).not.toContain('never double-spend')
      expect(text.toLowerCase()).not.toContain('kyc')
      expect(text.toLowerCase()).not.toContain('fiat')
    }
  })
})
