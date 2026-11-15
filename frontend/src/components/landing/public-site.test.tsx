import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { jsonLdGraph, publicDocuments } from '@/content/public-documents'
import { LandingFooter } from './chrome'
import { OPERATOR, OperatorContact } from './legal'
import { SISTER_SITES } from './sisters'
import { WorkflowFunnelGate } from './workflow-gate'

const STREET =
  /\b\d{1,6}\s+(?:[NSEW]\.?\s+)?(?:[A-Za-z0-9.'#-]+\s+){0,4}(?:Street|St|Avenue|Ave|Boulevard|Blvd|Road|Rd|Drive|Dr|Lane|Ln|Court|Ct|Place|Pl|Terrace|Ter|Circle|Cir|Way|Trail|Trl|Parkway|Pkwy)\b\.?/gi
const ZIP = /\b[A-Z]{2}\s+\d{5}(?:-\d{4})?\b|(?<![A-Za-z]\s)\b\d{5}(?:-\d{4})?\b/g
const PO_BOX = /\bP\.?\s*O\.?\s*Box\b/gi
const INVESTOR_LEAK = /\b(?:seed\s+round|valuation|funding\s+round|investor\s+deck|pre-money|post-money)\b|(?<![A-Za-z-])\barr\b/i

const frontendRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..')

function filesUnder(path: string): string[] {
  const full = join(frontendRoot, path)
  if (statSync(full).isFile()) return [full]
  return readdirSync(full, { withFileTypes: true }).flatMap((entry) => {
    const child = join(path, entry.name)
    if (entry.isDirectory()) return filesUnder(child)
    if (entry.name.endsWith('.test.ts') || entry.name.endsWith('.test.tsx')) return []
    return [join(frontendRoot, child)]
  })
}

const PUBLIC_FILES = [
  'src/app/page.tsx',
  'src/app/privacy',
  'src/app/terms',
  'src/app/architecture',
  'src/app/federation',
  'src/app/news',
  'src/app/blog',
  'src/app/notes',
  'src/app/workflows',
  'src/app/layout.tsx',
  'src/app/robots.ts',
  'src/app/sitemap.ts',
  'src/app/feed.xml',
  'src/components/landing',
  'src/content/public-documents.ts',
  'src/content/workflows.ts',
].flatMap(filesUnder)

describe('public sister links and quiet footer disclosure', () => {
  it('renders a follow-link footer and quiet simulation disclosure on the shared landing chrome', () => {
    render(<LandingFooter />)
    expect(screen.getByText('An Escherbridge project')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'NOTES' }).getAttribute('href')).toBe('/notes')
    expect(screen.getByText(/Hosted demo runs simulated payloads/)).toBeTruthy()
    expect(screen.getByText(/Real-value bridging and fiat settlement are disabled/)).toBeTruthy()
    expect(screen.getByText(/not legal advice/)).toBeTruthy()
    for (const site of SISTER_SITES) {
      const link = screen.getByRole('link', { name: site.label })
      expect(link.getAttribute('href')).toBe(site.href)
      expect(link.getAttribute('rel') ?? '').not.toContain('nofollow')
    }
    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href') ?? '')
    expect(hrefs.some((href) => href.includes('azoa.io'))).toBe(false)
    expect(hrefs).not.toContain('https://azoa.ardanova.com')
  })

  it('renders the funnel gate with clear navigation into architecture, workflows, console, and ArdaNova', () => {
    render(<WorkflowFunnelGate context="workflow" />)
    expect(screen.getByRole('link', { name: 'READ ARCHITECTURE →' }).getAttribute('href')).toBe('/architecture')
    expect(screen.getByRole('link', { name: 'ALL WORKFLOWS →' }).getAttribute('href')).toBe('/workflows')
    expect(screen.getByRole('link', { name: 'workspace sign-in' }).getAttribute('href')).toBe('/login')
    expect(screen.getByRole('link', { name: 'ArdaNova' }).getAttribute('href')).toBe('https://ardanova.com')
    expect(screen.getByRole('link', { name: 'GITHUB ↗' }).getAttribute('href')).toContain('github.com/JadeZaher/azoa')
  })

  it('keeps the homepage builder-first while connecting stack sites cleanly', () => {
    const home = readFileSync(join(frontendRoot, 'src/app/page.tsx'), 'utf8')
    const layout = readFileSync(join(frontendRoot, 'src/app/layout.tsx'), 'utf8')
    expect(home).toContain('WORKFLOW PATTERNS →')
    expect(home).toContain('ARCHITECTURE →')
    expect(home).toContain('HOSTED DEMO · SIMULATED PAYLOADS')
    expect(home).not.toContain('This public site is a simulation')
    expect(home).not.toContain('never double-spend')
    expect(home).toContain('https://primusneo.com')
    expect(home).toContain('https://ardanova.com')
    expect(layout).toContain('SHARE_IMAGE')
    expect(readFileSync(join(frontendRoot, 'src/content/public-documents.ts'), 'utf8')).toContain('/og/azoa-og-1200x630.png')
    expect(layout).toContain('/favicons/azoa-favicon-180.png')
    expect(readFileSync(join(frontendRoot, 'public/favicon.ico')).length).toBeGreaterThan(0)
  })
})

describe('operator contact and compliance boundaries', () => {
  it('equals the city and email form', () => {
    expect(`${OPERATOR.address} ${OPERATOR.email}`).toBe('Boise, Idaho contact@escherbridge.com')
    render(<OperatorContact />)
    expect(screen.getByText(OPERATOR.address).textContent).toBe('Boise, Idaho')
    expect(screen.getByRole('link', { name: OPERATOR.email }).textContent).toBe('contact@escherbridge.com')
  })

  it('finds no street, ZIP, PO Box, or investor/valuation figure on any public page', () => {
    const graphs = ['/', ...publicDocuments().map((document) => document.path)].map((path) =>
      JSON.stringify(jsonLdGraph(path)),
    )
    const texts = [...PUBLIC_FILES.map((file) => readFileSync(file, 'utf8')), ...graphs]
    const addressHits = texts.flatMap((text) =>
      [STREET, ZIP, PO_BOX].flatMap((pattern) => text.match(new RegExp(pattern.source, pattern.flags)) ?? []),
    )
    expect(addressHits.length).toBe(0)
    const investorHits = texts.flatMap((text) => text.match(INVESTOR_LEAK) ?? [])
    expect(investorHits.length).toBe(0)
  })
})
