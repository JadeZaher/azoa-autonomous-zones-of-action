import type { Metadata } from 'next'
import Link from 'next/link'
import { LandingFooter, LandingHead, LandingNav } from '@/components/landing/chrome'

export const metadata: Metadata = {
  title: 'Page not found — AZOA',
  description: 'This address does not match a public AZOA page.',
}

export default function NotFound() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav />
      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">404</div>
          <h1 className="az-h1">Page not found.</h1>
          <p className="az-fed-lede">This address does not match a public AZOA page.</p>
          <p>
            <Link href="/">Home</Link>
            {' · '}
            <Link href="/workflows">Workflows</Link>
            {' · '}
            <Link href="/contact">Contact</Link>
          </p>
        </div>
      </header>
      <LandingFooter />
    </div>
  )
}
