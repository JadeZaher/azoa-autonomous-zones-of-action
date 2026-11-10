import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { LandingFooter } from './chrome'
import { SISTER_SITES } from './sisters'

const frontendRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..')

describe('public sister links', () => {
  it('renders a follow-link footer on the shared landing chrome', () => {
    render(<LandingFooter />)
    expect(screen.getByText('An Escherbridge project')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'NOTES' }).getAttribute('href')).toBe('/notes')
    for (const site of SISTER_SITES) {
      const link = screen.getByRole('link', { name: site.label })
      expect(link.getAttribute('href')).toBe(site.href)
      expect(link.getAttribute('rel') ?? '').not.toContain('nofollow')
    }
    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href') ?? '')
    expect(hrefs.some((href) => href.includes('azoa.io'))).toBe(false)
    expect(hrefs).not.toContain('https://azoa.ardanova.com')
  })

  it('keeps the homepage simulation frame and the stack links', () => {
    const home = readFileSync(join(frontendRoot, 'src/app/page.tsx'), 'utf8')
    const layout = readFileSync(join(frontendRoot, 'src/app/layout.tsx'), 'utf8')
    expect(home).toContain('This public site is a simulation')
    expect(home).toContain('Real-value bridging is disabled')
    expect(home).not.toContain('never double-spend')
    expect(home).toContain('modelled to settle once, or not at all. This site does not move value.')
    expect(home).toContain('https://primusneo.com')
    expect(home).toContain('PrimusNeo, the lab whose stack this engine belongs to')
    expect(home).toContain('https://ardanova.com')
    expect(home).toContain('ArdaNova, a planned pilot whose economy is still simulated')
    expect(layout).toContain('/og/azoa-og-1200x630.png')
    expect(layout).toContain('/favicons/azoa-favicon-180.png')
    expect(readFileSync(join(frontendRoot, 'public/favicon.ico')).length).toBeGreaterThan(0)
  })
})
