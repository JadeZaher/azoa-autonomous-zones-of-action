import type { Metadata } from 'next'
import Link from 'next/link'
import { LegalDocument, LegalSection, OperatorContact } from '@/components/landing/legal'

export const metadata: Metadata = {
  title: 'Privacy — AZOA',
  description:
    'What the hosted AZOA site stores: account fields, browser storage, optional provider sign-in, and verification records if a session is started.',
}

export default function PrivacyPage() {
  return (
    <LegalDocument
      eyebrow="PRE-LAUNCH"
      title="Privacy"
      lede="This page lists data this site and its API actually store. It does not add a retention schedule or an age rule. The code does not publish either one for ordinary accounts."
    >
      <LegalSection kicker="01 — OPERATOR" title="Who holds it.">
        <OperatorContact />
        <p>
          The <Link href="/terms">terms</Link> cover use of the site. Real-value bridging is disabled on this hosted site, and fiat settlement is not enabled.
        </p>
      </LegalSection>

      <LegalSection kicker="02 — VISITORS" title="Public pages.">
        <p>
          The marketing pages do not ask you to create an account, and this frontend does not include an analytics or advertising script.
        </p>
        <p>
          Those pages load fonts from fonts.googleapis.com and fonts.gstatic.com. Your browser sends that request to Google, including the address of the page. This app does not record that request.
        </p>
      </LegalSection>

      <LegalSection kicker="03 — ACCOUNT" title="Registration and profile.">
        <p>Creating an account sends, and the API stores:</p>
        <ul>
          <li>a username</li>
          <li>an email address</li>
          <li>a hash of the password (the password itself is not stored)</li>
          <li>the time the account was created</li>
          <li>an active flag, a verified flag, and a role string, filled in by the server</li>
          <li>a search-visibility flag, off unless you turn it on</li>
        </ul>
        <p>
          The profile form can also save a first name, a last name, and a title. Sign-in sends the email and password so the hash can be checked. A tenant-provisioned account can also store that tenant&apos;s own user id and an opaque reference the tenant supplied.
        </p>
      </LegalSection>

      <LegalSection kicker="04 — BROWSER" title="Saved on your device.">
        <p>After sign-in, the browser stores:</p>
        <ul>
          <li>the sign-in credential, under the key azoa_token</li>
          <li>the user id, under the key azoa_user_id</li>
        </ul>
        <p>
          The network you pick is stored under azoa.network. Settings can store a provider name under azoa_provider_name. Payment, transfer, and related screens save a request key before the request is sent, and can also save an amount, a server id, a recipient, or a fee acceptance, under keys that start with azoa:.
        </p>
      </LegalSection>

      <LegalSection kicker="05 — PROVIDERS" title="Google and GitHub.">
        <p>
          Sign-in with Google or GitHub appears only when this deployment has that provider configured. The API then stores which provider, the provider&apos;s stable account id, the email and display name the provider sent when the account was linked, when it was linked, and the last sign-in through that link. The email is a snapshot. It is not the lookup key.
        </p>
        <p>
          The provider&apos;s own site receives the sign-in. During the redirect, the API sets a short-lived correlation cookie so it can match the return trip.
        </p>
      </LegalSection>

      <LegalSection kicker="06 — RECORDS" title="What else an account can store.">
        <p>
          A wallet record stores the network name, the address, an optional public key, an optional label, whether it is the default, and whether it is an external wallet or a platform wallet. A platform wallet can also store encrypted key material.
        </p>
        <p>
          An API key record stores the name you give it, a hash of the key, a short display prefix, the scopes, the allowed origins, when it was created, an optional expiry, a last-used time, and a revoked time if you revoke it. The raw key is not stored.
        </p>
        <p>
          A webhook registration stores the URL you enter and an encrypted signing secret.
        </p>
        <p>
          A consent grant stores the account that gave it, the account that received it, the permission names, where it came from, and an optional expiry.
        </p>
      </LegalSection>

      <LegalSection kicker="07 — OPERATOR" title="Operator sign-in.">
        <p>
          Operator sign-in sends a username and a password. The session credential is stored in an HttpOnly cookie named azoa_operator_session, limited to /api/operator. Its lifetime is chosen by the deployment and cannot exceed 30 minutes.
        </p>
      </LegalSection>

      <LegalSection kicker="08 — IDENTITY" title="Verification, if you start it.">
        <p>
          The verify-identity screen can open a session with a verification provider. Real-money use of that check is not enabled on this hosted site. If a session is started, the API stores a submission: your account, an optional tenant, the provider name, a status, a reviewer id, review notes, a rejection reason, a provider session id, a verification link, session instructions, and the provider result text the server records.
        </p>
        <p>
          A person record, when one is written, stores a keyed hash of the document number and issuing state (not the raw document number), a first name, a last name, a birthday, and citizenship. It does not store document images, a personal number, or an address. A screening record, when one is written, stores the full name and birthday sent as the screening query, plus the verdict.
        </p>
        <p>
          The provider&apos;s own page receives whatever you submit there. This page does not state how long those records are kept. The code sets an expiry from deployment configuration and does not publish one fixed period here.
        </p>
      </LegalSection>
    </LegalDocument>
  )
}
