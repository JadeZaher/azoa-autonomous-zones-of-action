/** Reviewed workflow copy. Figures and source URLs are the drafts’; do not extend them. */

export const SIMULATION_SENTENCE =
  'This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.'

const AUTHOR_LINE: Block = {
  type: 'p',
  runs: ['Author: Escherbridge. Last reviewed: 2026-11-10, America/Denver.'],
}

const SIMULATION_CLOSE: Block = {
  type: 'p',
  runs: [
    { strong: SIMULATION_SENTENCE },
    ' ',
    { text: 'ArdaNova', href: 'https://ardanova.com' },
    ' is a planned pilot whose economy is still simulated. ',
    { text: 'PrimusNeo', href: 'https://primusneo.com' },
    ' is the lab whose stack this engine belongs to.',
  ],
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
  eyebrow: 'Thirteen workflows · the hosted site is a simulation',
  h1: 'Shapes the engine can describe.',
  seoTitle: 'Workflows | AZOA',
  description:
    'Workflow shapes the engine models: settle-once, fail-closed controls, consent records, idempotent retries, consent withdrawal, least privilege, separation of duties, event logging, BPMN diagrams, sagas, safe and idempotent methods, and consent that can be shown and distinguished. The hosted site is a simulation and does not move value.',
  publishedAt: PUBLISHED,
  lastReviewed: PUBLISHED,
  lede:
    'These pages are about workflow shapes the engine models: a retried step that settles once or not at all, a control that fails closed, and a consent record that can be shown and withdrawn. Five field notes read public rules the same way: RFC 9110 on idempotent retries, UK GDPR Article 7 on withdrawing consent, and NIST SP 800-53 Rev. 5 on least privilege (AC-6), separation of duties (AC-5), and which events an audit log is for (AU-2). Five more read the same way: BPMN 2.0.2 as a shared process diagram, Garcia-Molina and Salem on sagas, RFC 9110 on safe versus idempotent methods, and UK GDPR Article 7(1) and 7(2) on consent that can be shown and that is clearly distinguishable. The figures and legal sentences are other people’s, with the links left on. They are public practice, not a claim that this hosted site moves money, verifies identity for value, or onboarded a regulated customer.',
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
    { type: 'h2', text: 'Five field notes' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests', strong: true },
          ' — RFC 9110 §9.2.2: the intended effect of multiple identical requests is the same as one request, which is why a lost response can be retried automatically. PUT, DELETE, and safe methods are idempotent in that specification. This page does not call POST idempotent, and it does not invent idempotency keys.',
        ],
        [
          { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal', strong: true },
          ' — UK GDPR Article 7(3): consent can be withdrawn at any time, and it shall be as easy to withdraw as to give. Not legal advice.',
        ],
        [
          { text: 'Least privilege', href: '/workflows/least-privilege', strong: true },
          ' — NIST SP 800-53 Rev. 5, AC-6: only the authorized accesses a user or process needs to accomplish the assigned task. Not an 800-53 implementation claim.',
        ],
        [
          { text: 'Separation of duties', href: '/workflows/separation-of-duties', strong: true },
          ' — NIST SP 800-53 Rev. 5, AC-5: identify and document the duties that require separation, then define access authorizations that support that split.',
        ],
        [
          { text: 'Event logging', href: '/workflows/event-logging', strong: true },
          ' — NIST SP 800-53 Rev. 5, AU-2 starts by identifying the event types the system can log in support of the audit function. Later control items are not reconstructed here.',
        ],
      ],
    },
    { type: 'h2', text: 'Five more field notes' },
    {
      type: 'ul',
      items: [
        [
          { text: 'What is BPMN', href: '/workflows/workflow-state-machines', strong: true },
          ' — OMG BPMN 2.0.2, January 2014: a notation the people who design, manage, and realize a process can share. This page does not claim AZOA conformance.',
        ],
        [
          { text: 'What is a saga', href: '/workflows/compensating-transactions', strong: true },
          ' — Garcia-Molina and Salem: either all the transactions in a saga complete, or compensating transactions amend a partial execution. Not a recovery playbook.',
        ],
        [
          { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent', strong: true },
          ' — RFC 9110 §9.2.1: safe methods are essentially read-only. Safe methods are idempotent; PUT and DELETE are idempotent without being safe.',
        ],
        [
          { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent', strong: true },
          ' — UK GDPR Article 7(1): where processing is based on consent, the controller shall be able to demonstrate it. Not legal advice.',
        ],
        [
          { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent', strong: true },
          ' — UK GDPR Article 7(2), quoted in full: the request must be clearly distinguishable, and an infringing part shall not be binding. Not legal advice.',
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
const rfc9110 = 'https://www.rfc-editor.org/rfc/rfc9110.html'
const nist80053 = 'https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-53r5.pdf'
const nist80053Doi = 'https://doi.org/10.6028/NIST.SP.800-53r5'
const nist80053Csrc = 'https://csrc.nist.gov/publications/detail/sp/800-53/rev-5/final'

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
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — a lost response can be retried automatically only when two identical requests mean one effect. This site does not move value.',
          ],
          [
            { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
            ' — identify the duties that require separation, then define access authorizations that support the split.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events the audit function is for.',
          ],
          [
            { text: 'What is BPMN', href: '/workflows/workflow-state-machines' },
            ' — a notation the people who design, manage, and realize a process can share. This page does not claim AZOA conformance.',
          ],
          [
            { text: 'What is a saga', href: '/workflows/compensating-transactions' },
            ' — all the transactions complete, or compensating transactions amend a partial execution. Not a recovery playbook.',
          ],
          [
            { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent' },
            ' — safe methods are essentially read-only. Safe methods are idempotent; not every idempotent method is safe.',
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
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — a lost response can be retried automatically only when two identical requests mean one effect. This site does not move value.',
          ],
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — it shall be as easy to withdraw as to give consent. Not legal advice.',
          ],
          [
            { text: 'Least privilege', href: '/workflows/least-privilege' },
            ' — only the authorized accesses a user or process needs to accomplish the assigned task.',
          ],
          [
            { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
            ' — identify the duties that require separation, then define access authorizations that support the split.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events the audit function is for.',
          ],
          [
            { text: 'What is BPMN', href: '/workflows/workflow-state-machines' },
            ' — a notation the people who design, manage, and realize a process can share. This page does not claim AZOA conformance.',
          ],
          [
            { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent' },
            ' — safe methods are essentially read-only. Safe methods are idempotent; not every idempotent method is safe.',
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
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — it shall be as easy to withdraw as to give consent. Not legal advice.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events, including a withdrawal, the audit function is for.',
          ],
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — where processing is based on consent, the controller shall be able to demonstrate it. Not legal advice.',
          ],
          [
            { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
            ' — Article 7(2), quoted in full, including that an infringing part shall not be binding. Not legal advice.',
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
  {
    slug: 'idempotent-requests',
    title: 'Idempotent retries in a workflow',
    h1: 'Idempotent retries in a workflow',
    seoTitle: 'Idempotent retries in a workflow — Workflows | AZOA',
    description:
      'A lost workflow response can be retried automatically only when two identical requests mean one effect. This is the workflow reading of RFC 9110.',
    eyebrow: 'Idempotent requests',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'If the response never arrived, automatic retry is safe only when two identical requests were designed to mean one effect. That is the HTTP rule, read here as a workflow problem: a settlement step, a grant of access, or a recorded consent change that may be interrupted mid-flight.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, June 2022, §9.2.2.',
        urls: [rfc9110],
        datePublished: '2022-06',
      },
      {
        name: 'Protocol sibling: Idempotent HTTP requests.',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
        runs: [
          'Protocol sibling: ',
          { text: 'Idempotent HTTP requests', href: 'https://primusneo.com/guides/idempotent-requests' },
        ],
      },
      {
        name: 'Workflow sibling: Settle once.',
        urls: ['/workflows/settle-once'],
        runs: [
          'Workflow sibling: ',
          { text: 'Settle once', href: '/workflows/settle-once' },
        ],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'This is a workflow note about retries, not a payments-product page. The protocol definition lives on ',
          { text: "PrimusNeo's idempotent HTTP requests guide", href: 'https://primusneo.com/guides/idempotent-requests' },
          ', which uses the same RFC 9110 source. This page does not invent a second definition. The already-live sibling is ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          '. This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled. It does not move money, and it does not prove a settle-once model in the wild.',
        ],
      },
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'RFC 9110, HTTP Semantics, June 2022, §9.2.2:' },
            ' "A request method is considered \'idempotent\' if the intended effect on the server of multiple identical requests with that method is the same as the effect for a single such request." "Of the request methods defined by this specification, PUT, DELETE, and safe request methods are idempotent."',
          ],
          [
            { strong: 'Why the distinction exists:' },
            ' "Idempotent methods are distinguished because the request can be repeated automatically if a communication failure occurs before the client is able to read the server\'s response."',
          ],
          [
            { strong: 'The two limits in the same section:' },
            ' "A proxy MUST NOT automatically retry non-idempotent requests." "A client SHOULD NOT automatically retry a failed automatic retry."',
          ],
        ],
      },
      { type: 'h2', text: "The RFC rule, once, in the RFC's words" },
      {
        type: 'p',
        runs: [
          'Intended effect of many identical requests equals the effect of one. PUT, DELETE, and safe methods are idempotent in this specification. The RFC does not say that POST is idempotent, and this page does not say so either. It does not invent "idempotency keys" as if RFC 9110 defined that product pattern. The longer protocol reading, including the logging note and the dropped-connection example, is on the ',
          { text: 'PrimusNeo page', href: 'https://primusneo.com/guides/idempotent-requests' },
          '.',
        ],
      },
      {
        type: 'p',
        runs: [
          'On a workflow, the same habit matters when a step may be interrupted before a response is read. Automatic retry is a safe move only for a method designed so that two identical requests mean one effect. ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ' is the sibling for a step that should stop rather than guess. ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          ' is the modelled "once, or not at all" page. Neither of those pages, and not this one, describes a live rail, a currency, or a KYC step.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: [
          'This is a worked example, not a customer and not a transfer. A workflow step is sent, the response is lost, and a proxy retries it automatically. If the method was not idempotent, RFC 9110 already says that automatic retry was the wrong move. This site does not move value, so it does not prove the modelled alternative in production.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all. This site does not move value.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events the audit function is for.',
          ],
          [
            { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent' },
            ' — safe methods are essentially read-only. Safe methods are idempotent; not every idempotent method is safe.',
          ],
          [
            { text: 'What is BPMN', href: '/workflows/workflow-state-machines' },
            ' — a notation the people who design, manage, and realize a process can share. This page does not claim AZOA conformance.',
          ],
          [
            { text: 'What is a saga', href: '/workflows/compensating-transactions' },
            ' — all the transactions complete, or compensating transactions amend a partial execution. Not a recovery playbook.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read the RFC page, or ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'consent-withdrawal',
    title: 'Withdrawing consent',
    h1: 'Withdrawing consent under UK GDPR Article 7',
    seoTitle: 'Withdrawing consent under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'Under UK GDPR Article 7, consent can be withdrawn at any time, and it should be as easy to withdraw as to give. This covers that rule. Not legal advice.',
    eyebrow: 'Consent withdrawal',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'Under Article 7 of the UK version of the GDPR, a person has the right to withdraw consent at any time, must be told that before consent is given, and "It shall be as easy to withdraw as to give consent." If giving consent was one step and withdrawing it is a scavenger hunt, the official test in Article 7(3) has not been met.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7, UK version, legislation.gov.uk.',
        urls: [articleSeven],
      },
      {
        name: 'Sibling: Consent records.',
        urls: ['/workflows/consent-records'],
        runs: [
          'Sibling: ',
          { text: 'Consent records', href: '/workflows/consent-records' },
        ],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'This is a field note about taking consent back, not a compliance badge, not a cookie-banner review, and not legal advice. It is a plain reading of an official article, as a sibling to the already-live ',
          { text: 'consent records', href: '/workflows/consent-records' },
          ' page. It is not a claim that AZOA is a controller in any particular case, not a KYC or age-verification how-to, and not a U.S. law survey. It does not walk a reader through a filing or a DPIA, and it does not invent fines, time limits for honouring a withdrawal, or "GDPR requires a particular button."',
        ],
      },
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Regulation (EU) 2016/679, Article 7, "Conditions for consent,"' },
            ' as it appears on legislation.gov.uk (UK version). "1. Where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented to processing of his or her personal data."',
          ],
          [
            { strong: 'Article 7(2):' },
            ' "If the data subject\'s consent is given in the context of a written declaration which also concerns other matters, the request for consent shall be presented in a manner which is clearly distinguishable from the other matters, in an intelligible and easily accessible form, using clear and plain language. Any part of such a declaration which constitutes an infringement of this Regulation shall not be binding."',
          ],
          [
            { strong: 'Article 7(3):' },
            ' "The data subject shall have the right to withdraw his or her consent at any time. The withdrawal of consent shall not affect the lawfulness of processing based on consent before its withdrawal. Prior to giving consent, the data subject shall be informed thereof. It shall be as easy to withdraw as to give consent." Article 7(4): "When assessing whether consent is freely given, utmost account shall be taken of whether, inter alia, the performance of a contract, including the provision of a service, is conditional on consent to the processing of personal data that is not necessary for the performance of that contract." That last sentence is not turned into contract advice here.',
          ],
        ],
      },
      { type: 'h2', text: 'What Article 7 actually says' },
      {
        type: 'p',
        runs: [
          'Demonstrate consent (7(1)). Distinguish it in a longer declaration (7(2)). Withdraw at any time; prior processing stays lawful; tell the person beforehand; as easy to withdraw as to give (7(3)). A withdrawn consent is a workflow event that has to be recorded and then respected — without inventing a house state machine. ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          ' is the sibling for the record itself. ',
          { text: 'Event logging', href: '/workflows/event-logging' },
          ' is the sibling for deciding which events the audit function is for. ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ' is the sibling for a step that should stop when consent is no longer there.',
        ],
      },
      {
        type: 'p',
        runs: ['This public site is a simulation. It does not move money, and it does not enable identity checks for moving real money.'],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: [
          'This is a worked example, not a customer and not a filing. Consent was given in one click, and withdrawal is buried three menus deep behind a form that is not clearly about consent. Article 7(3)\'s "as easy to withdraw as to give" sentence is already the official test that design fails.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — a consent that cannot be found later is not yet a record.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events, including a withdrawal, the audit function is for.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — where processing is based on consent, the controller shall be able to demonstrate it. Not legal advice.',
          ],
          [
            { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
            ' — Article 7(2), quoted in full, including that an infringing part shall not be binding. Not legal advice.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read Article 7, or ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'least-privilege',
    title: 'Least privilege',
    h1: 'Least privilege: only the access the task needs',
    seoTitle: 'Least privilege — Workflows | AZOA',
    description:
      'NIST SP 800-53 Rev. 5, AC-6, asks for only the authorized accesses a user or process needs for the assigned task. This guide covers that official principle.',
    eyebrow: 'Least privilege',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'NIST SP 800-53 Rev. 5, control AC-6, tells organizations to "Employ the principle of least privilege, allowing only authorized accesses for users (or processes acting on behalf of users) that are necessary to accomplish assigned organizational tasks." If a role or a process can do more than the assigned task requires, the official principle has not been applied yet.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AC-6 Least Privilege.',
        urls: [nist80053, nist80053Doi, nist80053Csrc],
        runs: [
          'NIST SP 800-53 Revision 5, AC-6 Least Privilege. ',
          { text: nist80053, href: nist80053 },
          ' · DOI: ',
          { text: nist80053Doi, href: nist80053Doi },
          ' · CSRC: ',
          { text: nist80053Csrc, href: nist80053Csrc },
        ],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'This is a field principle about access, not a penetration-test post, not a certification, and not a complete catalogue of AC-6 enhancements (AC-6(1) and later were not used in the brief). It contains no exploit steps, no proofs of concept, and no "how to escalate." AZOA does not claim to implement 800-53, and this page does not invent a role catalogue. This public site is a simulation. It does not move money.',
        ],
      },
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'NIST SP 800-53 Revision 5, AC-6 Least Privilege.' },
            ' Control: "Employ the principle of least privilege, allowing only authorized accesses for users (or processes acting on behalf of users) that are necessary to accomplish assigned organizational tasks."',
          ],
          [
            { strong: 'Discussion, as opened:' },
            ' "Organizations employ least privilege for specific duties and systems. The principle of least privilege is also applied to system processes, ensuring that the processes have access to systems and operate at privilege levels no higher than necessary to accomplish organizational missions or business functions. Organizations consider the creation of additional processes, roles, and accounts as necessary to achieve least privilege. Organizations apply least privilege to the development, implementation, and operation of organizational systems."',
          ],
        ],
      },
      { type: 'h2', text: 'What AC-6 actually says' },
      {
        type: 'p',
        runs: [
          'Authorized accesses only; users and processes; no higher privilege than the task or function needs; additional roles may be created in order to achieve least privilege; applies through development, implementation, and operation. A workflow can name the task and the access it actually needs. ',
          { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
          ' is the sibling for splitting duties that should not sit in one place. ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ' is the already-live sibling for a step that should stop rather than continue with too much access.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: [
          'This is a worked example, not a customer and not an audit. A workflow process is granted a broad administrative role because that was the easiest existing account, and the assigned task only needed to record a consent change. AC-6\'s "necessary to accomplish assigned organizational tasks" sentence is already the reason that role is too wide.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
            ' — first name the duties that must be separated, then make the access authorizations support the split.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — decide which events the audit function is for.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to name the task and the access it actually needs. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'separation-of-duties',
    title: 'Separation of duties',
    h1: 'Separation of duties',
    seoTitle: 'Separation of duties — Workflows | AZOA',
    description:
      'NIST SP 800-53 Rev. 5, AC-5, tells organizations to identify the duties that require separation, and to define access authorizations that support that split.',
    eyebrow: 'Separation of duties',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'NIST SP 800-53 Rev. 5, control AC-5, tells organizations to identify and document the duties that require separation, and to define system access authorizations that support that separation. First say which duties must be separated. Then make the access authorizations actually support that split.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AC-5 Separation of Duties.',
        urls: [nist80053, nist80053Doi],
        runs: [
          'NIST SP 800-53 Revision 5, AC-5 Separation of Duties. ',
          { text: nist80053, href: nist80053 },
          ' · DOI: ',
          { text: nist80053Doi, href: nist80053Doi },
        ],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'This is a field note about splitting duties, not a bank-operations manual, not a KYC page, and not a certification. It does not invent a mandatory initiator/approver pair as a NIST rule. The assignment in the control is organization-defined. It contains no exploit steps, and it does not invent a live settlement desk. This public site is a simulation. It does not move value. AZOA does not claim to implement 800-53.',
        ],
      },
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'NIST SP 800-53 Revision 5, AC-5 Separation of Duties.' },
            ' Control: "a. Identify and document [Assignment: organization-defined duties of individuals requiring separation]; and b. Define system access authorizations to support separation of duties."',
          ],
          [
            'Those two control items are the official text used here. No additional discussion sentences were taken from the PDF beyond what the brief opened, and none are invented.',
          ],
        ],
      },
      { type: 'h2', text: 'What AC-5 actually says' },
      {
        type: 'p',
        runs: [
          'Identify and document the duties — the organization defines them. Define system access authorizations to support the separation. A workflow can name the split it is using. Naming a split is not the same as claiming a 800-53 implementation, and it is not the same as running a live desk.',
        ],
      },
      {
        type: 'p',
        runs: [
          { text: 'Least privilege', href: '/workflows/least-privilege' },
          ' is the sibling for access that should not exceed the assigned task. ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          ' is the already-live modelled sibling for a retried step that should not finish twice. ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ' is the sibling for a step that should stop rather than continue.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: [
          'This is a worked example, not a customer and not a production desk. One person can both start and finish a modelled settlement step, and no one has written down whether those duties should be separated. AC-5 already starts with "identify and document." The organization still has to do that part; NIST does not hand over a universal pair.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Least privilege', href: '/workflows/least-privilege' },
            ' — only the authorized accesses the task actually needs, including for processes.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to name the duties that must be separated, then check whether the access authorizations actually support that split. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'event-logging',
    title: 'Event logging',
    h1: 'Event logging: decide which events, and who needs them',
    seoTitle: 'Event logging — Workflows | AZOA',
    description:
      'NIST SP 800-53 Rev. 5, AU-2, starts by asking which event types the system can log for the audit function. This guide covers that first official step.',
    eyebrow: 'Event logging',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'NIST SP 800-53 Rev. 5, control AU-2, starts by asking the organization to identify the types of events the system is capable of logging in support of the audit function. A log that was never designed — no decided event types, no one who needs to read it — is not yet an audit function.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AU-2 Event Logging.',
        urls: [nist80053, nist80053Doi],
        runs: [
          'NIST SP 800-53 Revision 5, AU-2 Event Logging. ',
          { text: nist80053, href: nist80053 },
          ' · DOI: ',
          { text: nist80053Doi, href: nist80053Doi },
        ],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'This is a field note about choosing what the audit log is for, not a monitoring-product page, and not a guide to capturing secrets. It contains no keyloggers, clipboard monitors, secret capture, or exploit steps. It does not invent a required retention period or a universal event catalogue. The assignment in the control is organization-defined: NIST does not, in the opened opening of AU-2, hand you a universal event list. This is not a certification, and AZOA does not claim to implement 800-53. This public site is a simulation. It does not move money.',
        ],
      },
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'NIST SP 800-53 Revision 5, AU-2 Event Logging.' },
            ' Control item (a), as opened and quoted complete: "Identify the types of events that the system is capable of logging in support of the audit function: [Assignment: organization-defined event types that the system is capable of logging]."',
          ],
          [
            { strong: 'The next item, as far as the opened brief quotes it,' },
            ' begins by asking the organization to coordinate the event logging function with other organizational entities that require the audit. The remainder of that item, and any later items in AU-2, were not quoted complete in the brief. They are not reconstructed here. If a later draft needs them, the PDF should be re-opened and the items copied complete.',
          ],
        ],
      },
      { type: 'h2', text: 'What AU-2 actually starts with' },
      {
        type: 'p',
        runs: [
          'Identify event types the system is capable of logging, in support of the audit function. The organization defines the types. Coordination with other entities that need the audit is the direction of the next item, without inventing the rest of its wording.',
        ],
      },
      {
        type: 'p',
        runs: [
          'A workflow can say which events it records — for example a consent change, a failed-closed stop, or a settlement attempt — as illustrations, labeled as such, not as a claimed 800-53 implementation. ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          ' and ',
          { text: 'withdrawing consent', href: '/workflows/consent-withdrawal' },
          ' are the consent siblings. ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          ' and ',
          { text: 'fail-closed controls', href: '/workflows/fail-closed' },
          ' are the already-live workflow siblings. ',
          { text: 'Least privilege', href: '/workflows/least-privilege' },
          ' is the access sibling.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: [
          'This is a worked example, not a customer and not a monitoring deployment. A system "logs everything," no one can say which event types support the audit function, and no one is named as needing to read the log. AU-2 already starts by asking for those decisions.',
        ],
      },
      { type: 'sources' },
      {
        type: 'p',
        runs: [
          'The remainder of AU-2 after the complete item (a), and after the incomplete opening of item (b), is not cited as quoted text.',
        ],
      },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — a withdrawal is a workflow event that has to be recorded and then respected.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — a consent that cannot be found later is not yet a record.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to name the event types the audit function needs, and who has to read them. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'workflow-state-machines',
    title: 'What is BPMN',
    h1: 'What is BPMN (Business Process Model and Notation)',
    seoTitle: 'What is BPMN (Business Process Model and Notation) — Workflows | AZOA',
    description:
      'OMG’s BPMN is a standard notation for people who draft a process, implement it, and manage it. This guide covers that shared diagram, not a house engine.',
    eyebrow: 'BPMN process diagrams',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'People often search "workflow state machine." The opened official source is OMG\'s Business Process Model and Notation, not a vendor state-machine tutorial. BPMN is used in the body; the address of this page keeps the query people type. If the only description of the workflow is in someone\'s head, the people who draft it, implement it, and manage it do not have a common picture. BPMN was written as a notation those three can share. This is a field note about a shared process diagram, not a workflow-product page, not a payments rail, and not a claim that AZOA conforms to BPMN.',
    sources: [
      {
        name: 'OMG BPMN catalog, Version 2.0.2.',
        urls: ['https://www.omg.org/spec/BPMN'],
        datePublished: '2014-01',
      },
      {
        name: 'Business Process Model and Notation (BPMN) Version 2.0.2, formal/2013-12-09.',
        urls: ['https://www.omg.org/spec/BPMN/2.0.2/PDF'],
        datePublished: '2013-12',
        runs: [
          { em: 'Business Process Model and Notation (BPMN) Version 2.0.2,' },
          ' formal/2013-12-09. ',
          { text: 'https://www.omg.org/spec/BPMN/2.0.2/PDF', href: 'https://www.omg.org/spec/BPMN/2.0.2/PDF' },
        ],
      },
    ],
    blocks: [
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'OMG catalog, current formal BPMN, Version 2.0.2, Document Status formal, Publication Date January 2014.' },
            ' "Business Process Model and Notation has become the de-facto standard for business processes diagrams. It is intended to be used directly by the stakeholders who design, manage and realize business processes, but at the same time be precise enough to allow BPMN diagrams to be translated into software process components. BPMN has an easy-to-use flowchart-like notation that is independent of any particular implementation environment." The earlier Version 2.0 catalog page (Publication Date December 2010) was also opened and states that it was superseded; 2.0.2 is the current formal inventory on the opened catalog.',
          ],
          [
            { strong: 'BPMN Version 2.0.2 specification PDF' },
            ' (OMG Document Number formal/2013-12-09; the PDF\'s date line is December 2013; "NOTE: Version 2.0.2 contains a minor change to Clause 15."). "The primary goal of BPMN is to provide a notation that is readily understandable by all business users, from the business analysts that create the initial drafts of the processes, to the technical developers responsible for implementing the technology that will perform those processes, and finally, to the business people who will manage and monitor those processes. Thus, BPMN creates a standardized bridge for the gap between the business process design and process implementation." "The intent of BPMN is to standardize a business process model and notation in the face of many different modeling notations and viewpoints. In doing so, BPMN will provide a simple means of communicating process information to other business users, process implementers, customers, and suppliers."',
          ],
          [
            { strong: 'The front matter' },
            ' also says the document "does not represent a commitment to implement any portion of this specification in any company’s products." Software may claim compliance "if and only if the software fully matches the applicable compliance points"; partial implementations "can claim only that the software was based on this International Standard, but cannot claim compliance or conformance." This page does not claim AZOA conformance.',
          ],
        ],
      },
      {
        type: 'p',
        runs: ['workflowpatterns.com did not open (HTTP 500 / failed fetch) on 2026-11-10 and is not used.'],
      },
      { type: 'h2', text: 'What this page does not invent' },
      {
        type: 'p',
        runs: [
          'No house state-machine, no BPMN element tutorial, and no settlement story. ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ', ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          ', and ',
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
          ' are the workflow siblings. ',
          { text: 'What is a saga in a long-lived transaction', href: '/workflows/compensating-transactions' },
          ' is the companion in this batch.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not a conformance claim. A workflow lives only in a chat thread, and three people later disagree about which step comes next. BPMN\'s primary-goal paragraph already names the three readers who needed a shared picture.'],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'What is a saga in a long-lived transaction', href: '/workflows/compensating-transactions' },
            ' — all complete, or compensating transactions amend a partial run.',
          ],
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — a lost response can be retried only when two identical requests mean one effect.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to draw a diagram both sides can read, or to read ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'compensating-transactions',
    title: 'What is a saga',
    h1: 'What is a saga in a long-lived transaction',
    seoTitle: 'What is a saga in a long-lived transaction — Workflows | AZOA',
    description:
      'A saga, in Garcia-Molina and Salem, is a long-lived transaction written as a sequence: all steps complete, or compensating transactions amend a partial run.',
    eyebrow: 'Sagas and compensating transactions',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'Garcia-Molina and Salem proposed the saga as a long-lived transaction written as a sequence of transactions: the system guarantees that either all of those transactions complete, or compensating transactions are run to amend a partial execution. It is not a licence to invent what happened after a failure. This is a definitional note, not a payments recovery tutorial, and not a recovery playbook.',
    sources: [
      {
        name: 'Hector Garcia-Molina and Kenneth Salem, Sagas, Department of Computer Science, Princeton University.',
        urls: ['https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf'],
        runs: [
          'Hector Garcia-Molina and Kenneth Salem, "Sagas," Department of Computer Science, Princeton University, opened at ',
          {
            text: 'https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf',
            href: 'https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf',
          },
        ],
      },
    ],
    blocks: [
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Hector Garcia-Molina and Kenneth Salem, "Sagas," Department of Computer Science, Princeton University.' },
            ' The opened file is a bitmapped scan of the paper at a Cornell course URL. The typography in automated extracts is degraded; only short, checkable sentences are used here. After ordinary typographic correction of the scan, the abstract reads: "Long lived transactions (LLTs) hold on to database resources for relatively long periods of time, significantly delaying the termination of shorter and more common transactions. To alleviate these problems we propose the notion of a saga. A LLT is a saga if it can be written as a sequence of transactions that can be interleaved with other transactions. The database management system guarantees that either all the transactions in a saga are successfully completed or compensating transactions are run to amend a partial execution." The opened file is cited as the source that was actually opened. A conference name or page range that is not on that file is not added. An ACM PDF of the same work returned HTTP 403 and was not used. Authors\' names appear with typographic ligature errors in the scan; the standard spelling Hector Garcia-Molina and Kenneth Salem is used, which is visible as the intended author line.',
          ],
        ],
      },
      { type: 'h2', text: 'What the paper actually says' },
      {
        type: 'p',
        runs: ['The abstract\'s guarantee is the whole of the definition used here: all complete, or compensating transactions amend a partial execution. Interleaving with other transactions is part of that definition. This page does not write implementation steps, locking advice, or a house recovery workflow from the rest of the scan. A saga is not a reason to invent a recovery narrative.'],
      },
      {
        type: 'p',
        runs: [
          { text: 'Settle once', href: '/workflows/settle-once' },
          ' and ',
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
          ' are the workflow siblings. ',
          { text: 'What is BPMN', href: '/workflows/workflow-state-machines' },
          ' is the shared-diagram companion. This page does not invent a client saga, a percentage, or a payments example that is not in the opened abstract.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not a transfer. A step fails mid-sequence, and a later note invents what "must have been compensated," with no record of a compensating transaction. The abstract already requires that a partial execution be amended by compensating transactions, not by a story written afterwards.'],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'What is BPMN', href: '/workflows/workflow-state-machines' },
            ' — a shared process diagram, not a house engine.',
          ],
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — a lost response can be retried only when two identical requests mean one effect.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read the opened abstract, or ',
          { text: 'Settle once', href: '/workflows/settle-once' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'safe-and-idempotent',
    title: 'Safe methods and idempotent methods',
    h1: 'Safe methods and idempotent methods',
    seoTitle: 'Safe methods and idempotent methods — Workflows | AZOA',
    description:
      'RFC 9110 distinguishes safe, essentially read-only methods from idempotent methods. Safe methods are idempotent; not every idempotent method is safe.',
    eyebrow: 'Safe and idempotent methods',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'RFC 9110 distinguishes methods whose defined semantics are essentially read-only (safe) from methods whose intended effect is the same for one request or for many identical ones (idempotent). Safe methods are idempotent; not every idempotent method is safe. "Safe to retry" and "safe to read" are not the same test. This is a short companion to Idempotent retries in a workflow and to PrimusNeo\'s RFC 9110 page. It quotes §9.2.1 here and does not invent a second definition of idempotency.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, §9.2.1 and §9.2.2.',
        urls: [rfc9110],
      },
      {
        name: 'Workflow sibling: Idempotent retries in a workflow.',
        urls: ['/workflows/idempotent-requests'],
        runs: [
          'Workflow sibling: ',
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
        ],
      },
      {
        name: 'Protocol sibling: Idempotent HTTP requests.',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
        runs: [
          'Protocol sibling: ',
          { text: 'Idempotent HTTP requests', href: 'https://primusneo.com/guides/idempotent-requests' },
        ],
      },
    ],
    blocks: [
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'RFC 9110, HTTP Semantics, §9.2.1 Safe Methods.' },
            ' "Request methods are considered ‘safe’ if their defined semantics are essentially read-only; i.e., the client does not request, and does not expect, any state change on the origin server as a result of applying a safe method to a target resource. Likewise, reasonable use of a safe method is not expected to cause any harm, loss of property, or unusual burden on the origin server." The definition "does not prevent an implementation from including behavior that is potentially harmful, that is not entirely read-only, or that causes side effects while invoking a safe method. What is important, however, is that the client did not request that additional behavior and cannot be held accountable for it." Of the methods defined by the specification, "GET, HEAD, OPTIONS, and TRACE methods are defined to be safe." "The purpose of distinguishing between safe and unsafe methods is to allow automated retrieval processes (spiders) and cache performance optimization (pre-fetching) to work without fear of causing harm." "If the purpose of such a resource is to perform an unsafe action, then the resource owner MUST disable or disallow that action when it is accessed using a safe request method." The RFC\'s example is a URI such as `page?do=delete` reached with GET.',
          ],
          [
            { strong: 'RFC 9110, §9.2.2 Idempotent Methods, for the contrast already used in batch 1.' },
            ' "A request method is considered ‘idempotent’ if the intended effect on the server of multiple identical requests with that method is the same as the effect for a single such request." "PUT, DELETE, and safe request methods are idempotent." So every safe method in this specification is idempotent; PUT and DELETE are idempotent without being safe. "A client SHOULD NOT automatically retry a request with a non-idempotent method unless it has some means to know that the request semantics are actually idempotent… or some means to detect that the original request was never applied." "A proxy MUST NOT automatically retry non-idempotent requests." "A client SHOULD NOT automatically retry a failed automatic retry."',
          ],
        ],
      },
      { type: 'h2', text: 'What the RFC actually says' },
      {
        type: 'p',
        runs: ['Safe means essentially read-only from the client\'s request. Side effects the client did not request do not make the method unsafe. GET, HEAD, OPTIONS, and TRACE are safe in this specification. Idempotent means one request or many identical ones have the same intended effect. PUT, DELETE, and the safe methods are idempotent. Do not automatically retry a non-idempotent method; a proxy must not do so; do not automatically retry a failed automatic retry. POST is not called safe or idempotent here. High-level concepts only — no attack recipes.'],
      },
      {
        type: 'p',
        runs: [
          { text: 'Settle once', href: '/workflows/settle-once' },
          ' and ',
          { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
          ' are the workflow siblings.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not a transfer. A workflow treats every "retryable" call as if it were a read, and a GET is used to trigger a delete. RFC 9110 already says that a resource whose purpose is an unsafe action must disable that action when it is reached with a safe method, and that safe and idempotent are not the same test.'],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — a lost response can be retried only when two identical requests mean one effect.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — a retried step is modelled to settle once, or not at all.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — a warning is not a control if the step can continue anyway.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read the RFC sections, or ',
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'demonstrable-consent',
    title: 'Consent must be demonstrable',
    h1: 'Consent must be demonstrable under UK GDPR Article 7',
    seoTitle: 'Consent must be demonstrable under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'UK GDPR Article 7(1) says that where processing is based on consent, the controller must be able to demonstrate that the person consented to it.',
    eyebrow: 'Demonstrable consent',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'UK GDPR Article 7(1) says that where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented to processing of his or her personal data. If processing is based on consent, the duty is to be able to show it. This is a short companion to Withdrawing consent under UK GDPR Article 7 and to Consent records. It is not legal advice about a specific entity, and not a compliance-product page.',
    sources: [
      {
        name: 'UK GDPR Article 7.',
        urls: [articleSeven],
      },
    ],
    blocks: [
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'UK version of GDPR, Article 7(1), legislation.gov.uk.' },
            ' "Where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented to processing of his or her personal data." That complete sentence is the whole of the duty quoted here. Article 7(3), already used in the withdrawal brief, remains the companion duty: consent may be withdrawn at any time, and it shall be as easy to withdraw as to give consent. Article 7(2) has its own sibling in this batch and is not summarised here.',
          ],
        ],
      },
      { type: 'h2', text: 'What the article actually says' },
      {
        type: 'p',
        runs: [
          'If you rely on consent, you have to be able to show it. This page does not invent a house log format, a demonstration standard, or a retention period that is not in the opened sentence. ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          ' is the sibling for what a record can hold. ',
          { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
          ' is the sibling for a request buried in a longer declaration. ',
          { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
          ' is the companion for 7(3).',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not legal advice. A tick-box exists somewhere, and no record remains of who agreed to what, or when. Article 7(1) already requires the controller to be able to demonstrate the consent.'],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
            ' — the request cannot be hidden in a longer declaration.',
          ],
          [
            { text: 'Withdrawing consent under UK GDPR Article 7', href: '/workflows/consent-withdrawal' },
            ' — it shall be as easy to withdraw as to give consent.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — a consent that cannot be found later is not yet a record.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read Article 7, or ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
        ],
      },
      AUTHOR_LINE,
    ],
  },
  {
    slug: 'distinguishable-consent',
    title: 'Consent must be clearly distinguishable',
    h1: 'Consent must be clearly distinguishable under UK GDPR Article 7',
    seoTitle: 'Consent must be clearly distinguishable under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'If consent sits inside a longer written declaration, Article 7(2) requires the request to be clearly distinguishable, in clear and plain language.',
    eyebrow: 'Distinguishable consent',
    publishedAt: PUBLISHED,
    lastReviewed: PUBLISHED,
    lede:
      'If consent is given in a written declaration that also concerns other matters, Article 7(2) requires the request for consent to be clearly distinguishable from those other matters, in an intelligible and easily accessible form, using clear and plain language. This is a short companion to Consent must be demonstrable and to Withdrawing consent. It is not legal advice, and not a design-system page.',
    sources: [
      {
        name: 'UK GDPR Article 7.',
        urls: [articleSeven],
      },
    ],
    blocks: [
      { type: 'h2', text: 'Hero figures' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'UK version of GDPR, Article 7(2), legislation.gov.uk — complete text.' },
            ' "If the data subject\'s consent is given in the context of a written declaration which also concerns other matters, the request for consent shall be presented in a manner which is clearly distinguishable from the other matters, in an intelligible and easily accessible form, using clear and plain language. Any part of such a declaration which constitutes an infringement of this Regulation shall not be binding." Both sentences are quoted. The second sentence is not abridged.',
          ],
          [
            { strong: 'Article 7(4), quoted completely as a neighbouring test:' },
            ' "When assessing whether consent is freely given, utmost account shall be taken of whether, inter alia, the performance of a contract, including the provision of a service, is conditional on consent to the processing of personal data that is not necessary for the performance of that contract."',
          ],
        ],
      },
      { type: 'h2', text: 'What the article actually says' },
      {
        type: 'p',
        runs: [
          'If the consent sits inside a longer declaration, the request has to be clearly distinguishable from the other matters, and in clear and plain language. An infringing part of such a declaration "shall not be binding." This page does not invent a house layout or a typography rule beyond that official wording. ',
          { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
          ' is the 7(1) sibling. ',
          { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
          ' is the 7(3) sibling. ',
          { text: 'Consent records', href: '/workflows/consent-records' },
          ' is the record sibling.',
        ],
      },
      { type: 'h2', text: 'An illustrative scenario' },
      {
        type: 'p',
        runs: ['This is a worked example, not a customer and not legal advice. A "consent" is buried in a longer terms document, with no distinguishable request, and is later treated as binding in full. Article 7(2) already says the request has to be clearly distinguishable, and that an infringing part shall not be binding.'],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — if you rely on consent, you have to be able to show it.',
          ],
          [
            { text: 'Withdrawing consent under UK GDPR Article 7', href: '/workflows/consent-withdrawal' },
            ' — it shall be as easy to withdraw as to give consent.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — a consent that cannot be found later is not yet a record.',
          ],
        ],
      },
      SIMULATION_CLOSE,
      {
        type: 'p',
        runs: [
          'The useful next step is to read Article 7, or ',
          { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
          '. If something on this page is wrong, ',
          { text: 'write to us', href: '/' },
          '.',
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

export function sourceLinksOwnUrls(source: WorkflowSource): boolean {
  if (!source.runs || source.linkLabel) return false
  const hrefs = new Set(
    source.runs.flatMap((run) => (typeof run === 'object' && 'href' in run ? [run.href] : [])),
  )
  return source.urls.length > 0 && source.urls.every((url) => hrefs.has(url))
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
        if (!sourceLinksOwnUrls(source)) parts.push(source.linkLabel ?? source.urls.join(' '))
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
