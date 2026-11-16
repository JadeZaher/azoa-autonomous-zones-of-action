import Link from 'next/link'
import { LegalDocument, LegalSection, OPERATOR } from '@/components/landing/legal'
import { SIMULATION } from '@/content/public-documents'

export default function ContactPage() {
  return (
    <LegalDocument
      eyebrow="OPERATOR"
      title="Contact"
      lede={SIMULATION}
    >
      <LegalSection kicker="01 — WRITE" title="Write to the operator.">
        <p>Escherbridge, LLC, Boise, Idaho</p>
        <p><a href={`mailto:${OPERATOR.email}`}>{OPERATOR.email}</a></p>
        <p><a href={OPERATOR.phoneHref}>{OPERATOR.phone}</a></p>
        <p>There is no form on this page.</p>
        <p>
          <Link href="/privacy">Privacy</Link>
          {' · '}
          <Link href="/terms">Terms</Link>
        </p>
      </LegalSection>
    </LegalDocument>
  )
}
