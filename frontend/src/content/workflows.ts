/** Reviewed workflow copy. Figures and source URLs are the drafts’; do not extend them. */

export const SIMULATION_SENTENCE =
  'This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.'

const AUTHOR_LINE: Block = {
  type: 'p',
  runs: ['Author: Escherbridge. Last reviewed: 2026-11-10, America/Denver.'],
}

const PUBLISHED = '2026-11-10'
const NOTE_DATE = '2026-11-09'

export type Run =
  | string
  | { text: string; href: string; strong?: boolean }
  | { strong: string }
  | { em: string }

export type Block =
  | { type: 'p'; runs: readonly Run[] }
  | { type: 'h2'; text: string }
  | { type: 'ul'; items: readonly (readonly Run[])[] }
  | { type: 'sources' }

export type WorkflowSource = {
  name: string
  urls: readonly string[]
  datePublished?: string
  linkLabel?: string
  runs?: readonly Run[]
}

export type WorkflowPage = {
  slug: string
  title: string
  h1: string
  seoTitle: string
  description: string
  eyebrow: string
  publishedAt: string
  lastReviewed: string
  lede: string
  sources: readonly WorkflowSource[]
  blocks: readonly Block[]
}

export const WORKFLOW_INDEX = {
  eyebrow: 'Three workflows · the hosted site is a simulation',
  h1: 'Shapes the engine can describe.',
  seoTitle: 'Workflows | AZOA',
  description:
    'Workflow shapes the engine models: settle-once, fail-closed controls, and consent records. The hosted site is a simulation and does not move value.',
  publishedAt: PUBLISHED,
  lastReviewed: PUBLISHED,
  lede:
    'These pages are about workflow shapes the engine models: a retried step that settles once or not at all, a control that fails closed, and a consent record that can be shown and withdrawn. The figures and legal sentences are other people’s, with the links left on. They are public practice, not a claim that this hosted site moves money, verifies identity for value, or onboarded a regulated customer.',
  blocks: [
    {
      type: 'p',
      runs: [
        { strong: SIMULATION_SENTENCE },
        ' That sentence belongs on this index and on every child, in the body, not only in the footer.',
      ],
    },
    { type: 'h2', text: 'The first three' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Settle once', href: '/workflows/settle-once', strong: true },
          ' — Stripe API v1 saves the first request’s status and body, including 500 errors, and returns that response on retry; FedNow requires a unique message ID and tells participants not to process a duplicate. A timeout, on its own, is not a decision.',
        ],
        [
          { text: 'Fail-closed controls', href: '/workflows/fail-closed', strong: true },
          ' — Saltzer and Schroeder, 1975: base access on permission, not exclusion, because a mistaken refusal is noticed and a mistaken allowance may not be. A warning on a screen is not a substitute for that rule.',
        ],
        [
          { text: 'Consent records', href: '/workflows/consent-records', strong: true },
          ' — UK GDPR Article 7: demonstrate consent, ask clearly, and make withdrawal as easy as the original yes. Permission that cannot be shown, or withdrawn, was not really recorded.',
        ],
      ],
    },
    { type: 'h2', text: 'What AZOA is doing here' },
    {
      type: 'p',
      runs: [
        'AZOA is an open-source engine in the ',
        { text: 'PrimusNeo', href: 'https://primusneo.com' },
        ' stack, not a consumer bank. Related house notes are ',
        { text: 'A retried step should settle once, or not at all', href: '/notes/settle-once-or-not-at-all' },
        ' and ',
        { text: 'Fail closed, or it isn’t a control', href: '/notes/fail-closed-or-it-isnt-a-control' },
        '. ',
        { text: 'ArdaNova', href: 'https://ardanova.com' },
        ' is a planned pilot whose economy is still simulated. Read ',
        { text: 'Architecture', href: '/architecture' },
        ' and the ',
        { text: 'Terms', href: '/terms' },
        '. Site notes, when there is something true to announce, live in ',
        { text: 'News', href: '/news' },
        '.',
      ],
    },
    AUTHOR_LINE,
  ] as readonly Block[],
}

const settleOnceSources: readonly WorkflowSource[] = [
  {
    name: 'Stripe Docs, “Idempotent requests” (API v1 wording; v2 distinguished).',
    urls: ['https://docs.stripe.com/api/idempotent_requests'],
  },
  {
    name: 'Federal Reserve Banks, FedNow Service Operating Procedures, Version 3.2, June 2025.',
    urls: ['https://www.frbservices.org/wp-content/uploads/062425-fednow-service-operating-procedures.pdf'],
    datePublished: '2025-06',
    runs: [
      'Federal Reserve Banks, ',
      { em: 'FedNow Service Operating Procedures,' },
      ' Version 3.2, June 2025.',
    ],
  },
  {
    name: 'AZOA, “A retried step should settle once, or not at all,” 9 November 2026 (sibling essay).',
    urls: ['https://azoa.ardanova.com/notes/settle-once-or-not-at-all'],
    datePublished: NOTE_DATE,
  },
  {
    name: 'AZOA homepage (public site is a simulation; does not move money; fiat not enabled).',
    urls: ['https://azoa.ardanova.com/'],
  },
  {
    name: 'AZOA Terms of use.',
    urls: ['https://azoa.ardanova.com/terms'],
  },
  {
    name: 'PrimusNeo, “Paid twice, or never” (sibling; current v1 wording).',
    urls: ['https://primusneo.com/blog/paid-twice-or-never'],
  },
  {
    name: 'PrimusNeo demo, “Paid twice.”',
    urls: ['https://primusneo.com/demos/paid-twice'],
  },
]

const failClosedSources: readonly WorkflowSource[] = [
  {
    name: 'Saltzer & Schroeder, “The Protection of Information in Computer Systems,” 1975 (fail-safe defaults).',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/Basic.html'],
    datePublished: '1975',
  },
  {
    name: 'Same paper, front matter / about (manuscript received 11 October 1974; revised 17 April 1975).',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/Meta.html'],
    datePublished: '1975',
  },
  {
    name: 'Same paper, index page.',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/'],
    datePublished: '1975',
  },
  {
    name: 'AZOA, “Fail closed, or it isn’t a control,” 9 November 2026 (sibling essay).',
    urls: ['https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control'],
    datePublished: NOTE_DATE,
  },
  {
    name: 'AZOA homepage (simulation; refuse what cannot be verified; does not move money).',
    urls: ['https://azoa.ardanova.com/'],
  },
  {
    name: 'AZOA Terms of use.',
    urls: ['https://azoa.ardanova.com/terms'],
  },
  {
    name: 'AZOA, “A retried step should settle once, or not at all,” 9 November 2026 (a timeout is also not a yes).',
    urls: ['https://azoa.ardanova.com/notes/settle-once-or-not-at-all'],
    datePublished: NOTE_DATE,
  },
]

const articleSeven = 'https://www.legislation.gov.uk/eur/2016/679/article/7'

const consentSources: readonly WorkflowSource[] = [
  {
    name: 'UK GDPR Article 7(1) (demonstrate consent).',
    urls: [articleSeven],
  },
  {
    name: 'UK GDPR Article 7(2) (distinguishable, clear, plain-language request).',
    urls: [articleSeven],
    linkLabel: 'Same URL.',
  },
  {
    name: 'UK GDPR Article 7(3) (withdrawal at any time; as easy to withdraw as to give).',
    urls: [articleSeven],
    linkLabel: 'Same URL.',
  },
  {
    name: 'UK GDPR Article 7(4) (freely given; not bundled with unnecessary conditions).',
    urls: [articleSeven],
    linkLabel: 'Same URL.',
  },
  {
    name: 'AZOA homepage and Terms (simulation; does not move money; no live fiat or identity-for-value checks).',
    urls: ['https://azoa.ardanova.com/', 'https://azoa.ardanova.com/terms'],
  },
  {
    name: 'AZOA, “Fail closed, or it isn’t a control,” 9 November 2026 (consent that can be seen and revoked, as house aim).',
    urls: ['https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control'],
    datePublished: NOTE_DATE,
  },
]

export const WORKFLOWS: readonly WorkflowPage[] = [
  {
    slug: 'settle-once',
    title: 'Settle once',
    h1: 'A timeout is not a decision. A retry should not become a second settlement.',
    seoTitle: 'Settle once — Workflows | AZOA',
    description:
      'A retried step should settle once, or not at all. Stripe API v1 and FedNow already name that habit. This hosted site does not move money.',
    eyebrow: 'Settle once',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'Idempotency is how a retried request stays one result. A missing reply is a silence rather than a result. Public payment systems already have a name for the habit that stops a silence from becoming a double payout: save the first result, or refuse a duplicate identifier. AZOA is being built to keep that kind of record, and this hosted site still does not move value.',
    sources: settleOnceSources,
    blocks: [
      {
        type: 'p',
        runs: ['Nobody has to be cheating; a timeout and a retry will do it. This page is not a thriller about lost payments, and the simulation sentence is not a footnote.'],
      },
      {
        type: 'p',
        runs: [
          { strong: SIMULATION_SENTENCE },
          ' This hosted site does not move money, and identity checks for moving real money are not enabled. AZOA is not a Stripe integration, not a FedNow participant, and not a bank.',
        ],
      },
      { type: 'h2', text: 'Hero points' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Stripe’s current API documentation for idempotent requests:' },
            ' “For API v1, Stripe saves the status code and body of the first request made with a given idempotency key after endpoint execution begins. Retries with the same key return the saved response, including 500 errors.” The same page distinguishes API v2 (“retries don’t repeat successful operations. Stripe retries failed or partially failed work to completion when possible and returns an updated response”). Quote v1 as v1, and do not collapse the v2 sentence into it. This is a public payments-API explanation of settle-once behaviour, not a claim that this site charges a card.',
          ],
          [
            { strong: 'FedNow Service Operating Procedures, Version 3.2 (June 2025):' },
            ' “FedNow Participants must use a unique message identifier for ISO 20022 messages, including when resubmitting a message that was previously rejected. The FedNow Service rejects messages that do not have a unique ID, responding with a message reject (admi.002) and applicable reason code.” If the participant is unsure of status after the payment timeout clock has expired, the procedures say to inquire first (pacs.028 or the Adhoc Query Tool); only if the Service likely did not process the message, and the participant wants to resend, “they should ensure it includes a new unique message ID.” Separately: “Participants must validate that all messages received from the FedNow Service have a unique message ID,” and if a duplicate arrives, “do not process” it, “in order to avoid processing the message more than once.”',
          ],
          [
            { strong: 'The live AZOA homepage and Terms already state the product limit,' },
            ' and this page does not upgrade it. The sibling note of 9 November 2026 already says a timeout is not a decision and that a retried step should settle once, or not at all. Those are site facts, not Stripe or FedNow metrics.',
          ],
        ],
      },
      {
        type: 'p',
        runs: [
          'The PrimusNeo essay ',
          { text: 'Paid twice, or never', href: 'https://primusneo.com/blog/paid-twice-or-never' },
          ' and the demo at ',
          { text: 'Paid twice', href: 'https://primusneo.com/demos/paid-twice' },
          ' now carry the same current Stripe API v1 wording. They are siblings in the stack, not the primary source; Stripe and FedNow carry the operational facts.',
        ],
      },
      { type: 'h2', text: 'The bill for a split system' },
      {
        type: 'ul',
        items: [
          [{ strong: 'Treating a timeout as a no, and paying again.' }],
          [{ strong: 'Treating a timeout as a yes, and leaving the other party unpaid, with two complete-looking logs.' }],
          [
            { strong: 'Describing this page, or this site, as a live payment rail, a Stripe integration, or a FedNow connection,' },
            ' or quoting a superseded Stripe sentence. Use the current API v1 wording, and keep API v2 distinct if it is mentioned. There is no KYC, Identomat, or onboarding-provider claim on this site.',
          ],
        ],
      },
      { type: 'h2', text: 'A shared record, applied here' },
      {
        type: 'ul',
        items: [
          [{ strong: 'Give a retried step a key that can be found later as a yes or a no.' }],
          [{ strong: 'Look up status before sending a second time.' }],
          [{ strong: 'Keep “does not move money” on the same screen as any payment-shaped example.' }],
        ],
      },
      { type: 'h2', text: 'What AZOA is, and is not, on this page' },
      {
        type: 'p',
        runs: [
          'This is an open-source workflow engine in the PrimusNeo stack, not a consumer bank. The public site is a simulation, and fiat settlement is not enabled. ',
          { text: 'ArdaNova', href: 'https://ardanova.com' },
          ' is a planned pilot whose economy is still simulated. This engine belongs to the stack at ',
          { text: 'PrimusNeo', href: 'https://primusneo.com' },
          '.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a live payout and not a customer. A grant-step times out and two operators each retry. The log should show one settlement or none, never two. Nothing on this site actually moves.'],
      },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — if a required check cannot be verified, the run should stop rather than warning and continuing.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — permission that cannot be shown, or withdrawn, was not recorded. It was only assumed.',
          ],
        ],
      },
      {
        type: 'p',
        runs: [
          'Related: ',
          { text: 'A retried step should settle once, or not at all', href: 'https://azoa.ardanova.com/notes/settle-once-or-not-at-all' },
          ', ',
          { text: 'Fail closed, or it isn’t a control', href: 'https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control' },
          ', ',
          { text: 'Architecture', href: 'https://azoa.ardanova.com/architecture' },
          ', ',
          { text: 'Paid twice, or never', href: 'https://primusneo.com/blog/paid-twice-or-never' },
          '.',
        ],
      },
      {
        type: 'p',
        runs: [
          'The useful next step is to read the ',
          { text: 'homepage', href: 'https://azoa.ardanova.com/' },
          ' and the ',
          { text: 'Terms', href: 'https://azoa.ardanova.com/terms' },
          ', and to leave the simulation sentence in the body while you do.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'fail-closed',
    title: 'Fail-closed controls',
    h1: 'If it cannot be verified, refuse it. A warning is not a control.',
    seoTitle: 'Fail-closed controls — Workflows | AZOA',
    description:
      'Fail-closed means permission, not exclusion, as Saltzer and Schroeder wrote in 1975. A warning in a log is not a control. This hosted site does not move value.',
    eyebrow: 'Fail-closed controls',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'Fail-closed is an old protection rule, and most systems would rather succeed than be right. Saltzer and Schroeder’s “fail-safe defaults” already said the safer default is lack of access, because a mistaken refusal is noticed and a mistaken allowance may not be. AZOA is being built that way. This hosted site still does not hold or move value, and that limit is part of the control.',
    sources: failClosedSources,
    blocks: [
      {
        type: 'p',
        runs: ['A control that fails open is a decoration. The sibling essay already has that line, and this page will not over-polish it. The Saltzer language below is kept in plain English.'],
      },
      {
        type: 'p',
        runs: [
          { strong: SIMULATION_SENTENCE },
          ' This public site is a simulation, and that sentence is part of the control, not a disclaimer under it. Fiat is not enabled; there is no live fiat bridge; identity checks for moving real money are not enabled.',
        ],
      },
      { type: 'h2', text: 'Hero points' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Jerome H. Saltzer and Michael D. Schroeder, “The Protection of Information in Computer Systems,”' },
            ' invited paper, manuscript received 11 October 1974 and revised 17 April 1975 (copyright 1975 by J. H. Saltzer; author-hosted HTML at MIT). Design principle (b), Fail-safe defaults: “Base access decisions on permission rather than exclusion.” “This principle, suggested by E. Glaser in 1965, means that the default situation is lack of access, and the protection scheme identifies conditions under which access is permitted.” “A conservative design must be based on arguments why objects should be accessible, rather than why they should not.” “In a large system some objects will be inadequately considered, so a default of lack of permission is safer.”',
          ],
          [
            { strong: 'The same principle continues:' },
            ' “A design or implementation mistake in a mechanism that gives explicit permission tends to fail by refusing permission, a safe situation, since it will be quickly detected. On the other hand, a design or implementation mistake in a mechanism that explicitly excludes access tends to fail by allowing access, a failure which may go unnoticed in normal use.” This is the warrant for “anything that cannot be verified is refused, not waved through.” A warning that does not stop the step is the unnoticed-failure case.',
          ],
          [
            { strong: 'Complete mediation, in the same paper:' },
            ' “Every access to every object must be checked for authority,” including “initialization, recovery, shutdown, and maintenance,” and proposals “to gain performance by remembering the result of an authority check” should be “examined skeptically.” Useful as a supporting habit (re-check; do not trust a remembered yes) without turning the page into a capability-system lecture.',
          ],
        ],
      },
      {
        type: 'p',
        runs: ['The live homepage and the 9 November 2026 sibling note already use house language that this page does not inflate. Those are site facts, not Saltzer metrics.'],
      },
      { type: 'h2', text: 'The bill for a split system' },
      {
        type: 'ul',
        items: [
          [{ strong: '“Log a warning and continue” when a check cannot be finished.' }],
          [
            { strong: 'Describing AZOA as a live custody, KYC, or payments-control product.' },
            ' There is no Identomat, no onboarding vendor, and no live fiat bridge on this site.',
          ],
          [
            { strong: 'Inventing a certification.' },
            ' Common Criteria, IEC 61508, NIST figures, and similar were not opened in the research pass and are not named as if they were.',
          ],
        ],
      },
      { type: 'h2', text: 'A shared record, applied here' },
      {
        type: 'ul',
        items: [
          [{ strong: 'Park or refuse a step that cannot be verified.' }],
          [{ strong: 'Treat the refusal as the recorded result, not as a temporary embarrassment.' }],
          [{ strong: 'Keep the simulation sentence in the body, not only in the footer.' }],
        ],
      },
      { type: 'h2', text: 'What AZOA is, and is not, on this page' },
      {
        type: 'p',
        runs: [
          'This is an open-source workflow engine in the ',
          { text: 'PrimusNeo', href: 'https://primusneo.com' },
          ' stack, not a consumer bank. ',
          { text: 'ArdaNova', href: 'https://ardanova.com' },
          ' is a planned pilot whose economy is still simulated. IEEE volume and issue numbers that were not opened are not cited; the MIT-hosted text and the dated Meta page are the source.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a live payment and not a customer. A grant-approval step is missing a required confirmation. The run stops and says no, rather than posting a warning and releasing a modelled payout. Nothing on this site actually moves.'],
      },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a timeout is not a decision, and a retry should not become a second settlement.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — permission that cannot be shown, or withdrawn, was not recorded. It was only assumed.',
          ],
        ],
      },
      {
        type: 'p',
        runs: [
          'Related: ',
          { text: 'Fail closed, or it isn’t a control', href: 'https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control' },
          ', ',
          { text: 'A retried step should settle once, or not at all', href: 'https://azoa.ardanova.com/notes/settle-once-or-not-at-all' },
          ', ',
          { text: 'Architecture', href: 'https://azoa.ardanova.com/architecture' },
          ', ',
          { text: 'Federation', href: 'https://azoa.ardanova.com/federation' },
          '.',
        ],
      },
      {
        type: 'p',
        runs: [
          'The useful next step is to read the ',
          { text: 'homepage', href: 'https://azoa.ardanova.com/' },
          ' and the ',
          { text: 'Terms', href: 'https://azoa.ardanova.com/terms' },
          ', including the simulation sentence in the body.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'consent-records',
    title: 'Consent records',
    h1: 'Permission that cannot be shown, or withdrawn, was not recorded. It was only assumed.',
    seoTitle: 'Consent records — Workflows | AZOA',
    description:
      'UK GDPR Article 7: consent must be demonstrable, clearly asked, and as easy to withdraw as to give. Public practice on this simulated site, not a KYC feature.',
    eyebrow: 'Consent records',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'A consent record has to show that permission was given, for what, and that it can be withdrawn as easily as it was given. Silence is not consent, and a tick buried in a longer document is not a record of one. UK GDPR Article 7 requires the controller to be able to demonstrate consent, to present the request clearly, and to honour withdrawal. That is a public consent-record practice — not a KYC vendor feature, and not a claim that this simulated AZOA site onboards anyone or moves money.',
    sources: consentSources,
    blocks: [
      {
        type: 'p',
        runs: ['AZOA is operated by Escherbridge, LLC in Idaho. This article is a public standard, not a declaration that the hosted site is a UK controller, that every workflow step is personal-data processing, or that publishing this page makes the hosted site GDPR-certified. This page does not give legal advice.'],
      },
      {
        type: 'p',
        runs: [
          { strong: SIMULATION_SENTENCE },
          ' This public site is a simulation and does not move money. Fiat settlement is not enabled, identity checks for moving real money are not enabled, and there is no Identomat or onboarding-provider claim here.',
        ],
      },
      { type: 'h2', text: 'Hero points' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'UK GDPR Article 7(1), official revised text on legislation.gov.uk:' },
            ' “Where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented to processing of his or her personal data.” A permission that cannot later be shown was not recorded in the sense the law uses.',
          ],
          [
            { strong: 'Article 7(2):' },
            ' if consent is given “in the context of a written declaration which also concerns other matters, the request for consent shall be presented in a manner which is clearly distinguishable from the other matters, in an intelligible and easily accessible form, using clear and plain language.” “Any part of such a declaration which constitutes an infringement of this Regulation shall not be binding.” A consent buried in terms, or bundled with an unrelated yes, fails this already-public test.',
          ],
          [
            { strong: 'Article 7(3):' },
            ' “The data subject shall have the right to withdraw his or her consent at any time.” “The withdrawal of consent shall not affect the lawfulness of processing based on consent before its withdrawal.” “Prior to giving consent, the data subject shall be informed thereof.” “It shall be as easy to withdraw as to give consent.” Withdrawal is part of the record, not an after-sales courtesy.',
          ],
          [
            { strong: 'Article 7(4):' },
            ' when assessing whether consent is freely given, “utmost account shall be taken of whether, inter alia, the performance of a contract, including the provision of a service, is conditional on consent to the processing of personal data that is not necessary for the performance of that contract.” Scope matters. A modelled workflow must not treat a required, unrelated permission as if it were freely given consent.',
          ],
        ],
      },
      {
        type: 'p',
        runs: ['The ICO’s detailed “how to obtain, record and manage consent” page could not be retrieved in the research pass (403). This page does not paraphrase ICO audit-trail field lists from memory.'],
      },
      {
        type: 'p',
        runs: ['The fail-closed sibling already mentions “consent that can be seen and revoked” as a construction aim — house language, not a compliance certificate. If NEOS is mentioned, its “consent” (a reasoned-objection check in ACT) is a different use of the word from GDPR consent.'],
      },
      { type: 'h2', text: 'The bill for a split system' },
      {
        type: 'ul',
        items: [
          [{ strong: 'Treating silence, or a continued run, as permission.' }],
          [{ strong: 'Bundling an unrelated permission into a contract or a terms tick.' }],
          [
            { strong: 'KYC, Identomat, onboarding-provider, or “this is our fiat identity check” claims,' },
            ' or quoting ICO field lists that were not opened.',
          ],
        ],
      },
      { type: 'h2', text: 'A shared record, applied here' },
      {
        type: 'ul',
        items: [
          [{ strong: 'Record who agreed, to what, and when, in a form that can be shown later.' }],
          [{ strong: 'Keep the request visually and linguistically separate from other matter.' }],
          [{ strong: 'Make withdrawal a first-class step, and record it; fail closed if the permission is no longer there.' }],
        ],
      },
      { type: 'h2', text: 'What AZOA is, and is not, on this page' },
      {
        type: 'p',
        runs: [
          'A simulated engine; no live custody; no vendor names; no implication that this page is a completed UK GDPR assessment. ',
          { text: 'ArdaNova', href: 'https://ardanova.com' },
          ' is a planned pilot whose economy is still simulated. This engine belongs to the stack at ',
          { text: 'PrimusNeo', href: 'https://primusneo.com' },
          '.',
        ],
      },
      { type: 'sources' },
      {
        type: 'p',
        runs: [
          'Privacy policy may be linked as a live page, not as a source of Article 7: ',
          { text: 'https://azoa.ardanova.com/privacy', href: 'https://azoa.ardanova.com/privacy' },
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not a live data-processing claim. A modelled grant workflow asks for a clearly separate yes to share a contact email with a reviewer, and later records a withdrawal that stops further sharing, without touching work already done. The permission can be shown, and it can be withdrawn. Nothing on this site moves money.'],
      },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a timeout is not a decision, and a retry should not become a second settlement.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — if a required check cannot be verified, the run should stop rather than warning and continuing.',
          ],
        ],
      },
      {
        type: 'p',
        runs: [
          'Related: ',
          { text: 'Fail closed, or it isn’t a control', href: 'https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control' },
          ', ',
          { text: 'A retried step should settle once, or not at all', href: 'https://azoa.ardanova.com/notes/settle-once-or-not-at-all' },
          ', ',
          { text: 'Architecture', href: 'https://azoa.ardanova.com/architecture' },
          ', ',
          { text: 'Terms', href: 'https://azoa.ardanova.com/terms' },
          ', ',
          { text: 'Privacy', href: 'https://azoa.ardanova.com/privacy' },
          '.',
        ],
      },
      {
        type: 'p',
        runs: [
          'The useful next step is to read the ',
          { text: 'homepage', href: 'https://azoa.ardanova.com/' },
          ' and the ',
          { text: 'Terms', href: 'https://azoa.ardanova.com/terms' },
          '. A modelled checkbox on this site is not a banking onboarding, and the page should not be read as one.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
]

export function workflowBySlug(slug: string): WorkflowPage | undefined {
  return WORKFLOWS.find((workflow) => workflow.slug === slug)
}

export function workflowCitations(sources: readonly WorkflowSource[]) {
  return sources.flatMap((source) =>
    source.urls.map((url) => ({
      '@type': 'CreativeWork' as const,
      name: source.name,
      url,
      ...(source.datePublished ? { datePublished: source.datePublished } : {}),
    })),
  )
}

function runText(run: Run): string {
  if (typeof run === 'string') return run
  if ('href' in run) return `${run.text} ${run.href}`
  if ('em' in run) return run.em
  return run.strong
}

export function blocksText(blocks: readonly Block[], sources: readonly WorkflowSource[] = []): string {
  const parts: string[] = []
  for (const block of blocks) {
    if (block.type === 'h2') parts.push(block.text)
    else if (block.type === 'p') parts.push(block.runs.map(runText).join(''))
    else if (block.type === 'ul') {
      parts.push(block.items.map((item) => item.map(runText).join('')).join('\n'))
    } else {
      parts.push('Sources')
      parts.push('Every stat on this page is cited.')
      for (const source of sources) {
        parts.push(source.runs ? source.runs.map(runText).join('') : source.name)
        parts.push(source.linkLabel ?? source.urls.join(' '))
      }
    }
  }
  return parts.join('\n')
}

export function indexBodyText(): string {
  return [WORKFLOW_INDEX.h1, WORKFLOW_INDEX.lede, blocksText(WORKFLOW_INDEX.blocks)].join('\n')
}

export function workflowBodyText(workflow: WorkflowPage): string {
  return [workflow.h1, workflow.lede, blocksText(workflow.blocks, workflow.sources)].join('\n')
}
