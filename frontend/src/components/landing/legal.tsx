import type { ReactNode } from 'react'
import { LandingFooter, LandingHead, LandingNav } from '@/components/landing/chrome'

export const OPERATOR = {
  name: 'Escherbridge, LLC',
  contact: 'Ahmed Zaher',
  email: 'contact@escherbridge.com',
  phone: '(509) 578-4603',
  phoneHref: 'tel:+15095784603',
  address: 'Boise, Idaho',
} as const

export function LegalDocument({
  eyebrow,
  title,
  lede,
  active,
  children,
}: {
  eyebrow: string
  title: string
  lede: string
  active?: 'architecture' | 'federation' | 'workflows'
  children: ReactNode
}) {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav active={active} />
      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">{eyebrow}</div>
          <h1 className="az-h1">{title}</h1>
          <p className="az-fed-lede">{lede}</p>
        </div>
      </header>
      {children}
      <LandingFooter />
    </div>
  )
}

export function LegalSection({
  kicker,
  title,
  children,
}: {
  kicker: string
  title: string
  children: ReactNode
}) {
  return (
    <section className="az-arch-section">
      <div className="az-arch-grid">
        <div className="az-kicker">{kicker}</div>
        <div className="az-arch-body">
          <h2 className="az-arch-h2">{title}</h2>
          {children}
        </div>
      </div>
    </section>
  )
}

export function OperatorContact() {
  return (
    <>
      <p>{OPERATOR.name}</p>
      <p>{OPERATOR.contact}</p>
      <p><a href={`mailto:${OPERATOR.email}`}>{OPERATOR.email}</a></p>
      <p><a href={OPERATOR.phoneHref}>{OPERATOR.phone}</a></p>
      <p>{OPERATOR.address}</p>
    </>
  )
}
