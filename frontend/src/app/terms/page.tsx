import type { Metadata } from 'next'
import Link from 'next/link'
import { LegalDocument, LegalSection, OperatorContact } from '@/components/landing/legal'

export const metadata: Metadata = {
  title: 'Terms — AZOA',
  description:
    'Pre-launch terms for the hosted AZOA site. The site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.',
}

export default function TermsPage() {
  return (
    <LegalDocument
      eyebrow="PRE-LAUNCH"
      title="Terms"
      lede="These terms cover use of this hosted AZOA website. The software is open source. This page is about the site Escherbridge, LLC operates, and it says what that site does today."
    >
      <LegalSection kicker="01 — THE SITE" title="A simulation.">
        <p>
          AZOA is an engine for structured workflows: identity, consent, and graphs of steps. This public site runs that engine as a simulation. Real-value bridging is disabled. Fiat settlement is not enabled. Identity checks for moving real money are not enabled.
        </p>
        <p>
          The site does not pay payroll, pay yield, send payouts, or run a live fiat bridge. Screens that mention those things describe the model. They do not move money.
        </p>
      </LegalSection>

      <LegalSection kicker="02 — ACCOUNTS" title="What you give us.">
        <p>
          Creating an account sends a username, an email address, and a password. The password is stored as a hash. Account recovery is not configured on this site. You keep the password.
        </p>
        <p>
          The profile form can also save a first name, a last name, and a title, and a switch for whether other signed-in people can find your username in search.
        </p>
      </LegalSection>

      <LegalSection kicker="03 — MONEY" title="Do not send funds here.">
        <p>
          Do not send money to this site, and do not expect a payment from it. A grant, a payroll line, or a payout drawn in the product is a record in a simulation until real-value bridging is enabled.
        </p>
      </LegalSection>

      <LegalSection kicker="04 — THE CODE" title="The license is separate.">
        <p>
          The source is licensed under Apache-2.0. These terms do not replace that license for someone who runs the software themselves. They cover this hosted site.
        </p>
        <p>
          This is pre-launch software. The site and these terms can change. The text on this page is the text published with the site.
        </p>
      </LegalSection>

      <LegalSection kicker="05 — CONTACT" title="Who operates it.">
        <OperatorContact />
        <p>
          Questions about these terms go to that address. The <Link href="/privacy">privacy page</Link> lists the data the site stores.
        </p>
      </LegalSection>

      <LegalSection kicker="06 — LAW" title="Idaho.">
        <p>These terms are governed by the laws of the State of Idaho, and any dispute will be resolved in the state or federal courts located in Ada County, Idaho.</p>
      </LegalSection>
    </LegalDocument>
  )
}
