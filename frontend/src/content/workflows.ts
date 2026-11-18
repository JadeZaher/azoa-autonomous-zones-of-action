/** Clear, non-technical guides for /workflows and /workflows/[slug] explaining what you can build with AZOA and why it exists. */

export const SIMULATION_SENTENCE =
  'Hosted demo runs simulated payloads. Real-value bridging and fiat settlement are disabled; self-host Apache-2.0 source for production workloads.'

const PUBLISHED = '2026-11-10'
const UPDATED = '2026-11-17'

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

const rfc9110 = 'https://www.rfc-editor.org/rfc/rfc9110.html'
const articleSeven = 'https://www.legislation.gov.uk/eur/2016/679/article/7'
const nist80053 = 'https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-53r5.pdf'
const nist80053Doi = 'https://doi.org/10.6028/NIST.SP.800-53r5'
const nist80053Csrc = 'https://csrc.nist.gov/publications/detail/sp/800-53/rev-5/final'
const azoaRepo = 'https://github.com/JadeZaher/azoa-autonomous-zones-of-action'
const azoaArchitecture = 'https://azoa.ardanova.com/architecture'

export const WORKFLOW_INDEX = {
  eyebrow: 'WHAT YOU CAN BUILD · 16 GUIDES',
  h1: 'One engine for real-world coordination.',
  seoTitle: 'Workflows | AZOA',
  description:
    'Plain-language guides to what you can build with AZOA — payouts, circular economies, grants, approvals, and self-owned permissions — and why the engine is built the way it is.',
  publishedAt: PUBLISHED,
  lastReviewed: UPDATED,
  lede:
    'Organizations run on multi-step promises: paying a supplier after work is checked, releasing a grant milestone, looping circular credits back into use, or waiting days for human sign-off. AZOA brings financial workflows, circular economies, operational approvals, and grant programs onto one durable engine so people own their identity, permissions stay clear, and nothing gets lost or paid twice.',
  blocks: [
    { type: 'h2', text: 'Reliable Money & Agreements' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Settle once', href: '/workflows/settle-once', strong: true },
          ' — why a retried payout or step settles once, or not at all, so nobody gets paid twice or left unpaid.',
        ],
        [
          { text: 'Check reality before retrying', href: '/workflows/reconciliation-over-blind-retry', strong: true },
          ' — when a partner goes silent mid-step, pause and confirm what actually happened instead of blindly firing again.',
        ],
        [
          { text: 'Safe retries across networks', href: '/workflows/idempotent-requests', strong: true },
          ' — keep flaky connections and double-clicks from creating duplicate transfers or approvals.',
        ],
        [
          { text: 'Reading safely vs. taking action', href: '/workflows/safe-and-idempotent', strong: true },
          ' — make sure checking a link or previewing a page never triggers an unintended payout or deletion.',
        ],
        [
          { text: 'Clean recovery when plans change', href: '/workflows/compensating-transactions', strong: true },
          ' — when step four fails, unwind earlier holds and commitments cleanly instead of patching spreadsheets by hand.',
        ],
      ],
    },
    { type: 'h2', text: 'Identity & Permission' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Consent records', href: '/workflows/consent-records', strong: true },
          ' — apps act for you only with clear permission you can inspect at any time.',
        ],
        [
          { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal', strong: true },
          ' — make saying no just as simple and immediate as saying yes.',
        ],
        [
          { text: 'Proof of permission', href: '/workflows/demonstrable-consent', strong: true },
          ' — keep a clear, lasting record of who approved what and under which terms.',
        ],
        [
          { text: 'Clear, separate choices', href: '/workflows/distinguishable-consent', strong: true },
          ' — never hide real permissions inside fine print or bundled checkboxes.',
        ],
        [
          { text: 'Only the access needed', href: '/workflows/least-privilege', strong: true },
          ' — give each step and app only the narrow permission required for its specific job.',
        ],
        [
          { text: 'Separation of duties', href: '/workflows/separation-of-duties', strong: true },
          ' — ensure no single person or script can both propose and rubber-stamp a high-stakes move.',
        ],
      ],
    },
    { type: 'h2', text: 'Trust, Waits & Clarity' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Fail-closed safeguards', href: '/workflows/fail-closed', strong: true },
          ' — anything that cannot be verified stops at the gate instead of waving through with a quiet warning.',
        ],
        [
          { text: 'Lock terms before approval', href: '/workflows/version-pinned-execution', strong: true },
          ' — guarantee that the terms someone reads on screen are the exact terms that execute.',
        ],
        [
          { text: 'Waiting patiently for real life', href: '/workflows/durable-waits-and-signals', strong: true },
          ' — workflows can pause for hours, days, or human reviews and pick right back up without losing their place.',
        ],
        [
          { text: 'An honest audit trail', href: '/workflows/event-logging', strong: true },
          ' — record the meaningful decisions — approvals, refusals, revocations — instead of drowning in noise.',
        ],
        [
          { text: 'Shared visual workflows', href: '/workflows/workflow-state-machines', strong: true },
          ' — draw processes as clear graphs so operators, reviewers, and builders agree on every branch.',
        ],
      ],
    },
    { type: 'h2', text: 'Across AZOA' },
    {
      type: 'p',
      runs: [
        'For the bigger picture behind why AZOA exists, read ',
        { text: 'A retried step should settle once, or not at all', href: '/notes/settle-once-or-not-at-all' },
        ' and ',
        { text: 'Fail closed, or it isn’t a control', href: '/notes/fail-closed-or-it-isnt-a-control' },
        '. To see how AZOA fits inside the ',
        { text: 'PrimusNeo', href: 'https://primusneo.com' },
        ' stack and powers the ',
        { text: 'ArdaNova', href: 'https://ardanova.com' },
        ' creator pilot, explore the ',
        { text: 'Architecture overview', href: '/architecture' },
        '.',
      ],
    },
  ] as readonly Block[],
}

const settleOnceSources: readonly WorkflowSource[] = [
  {
    name: 'Stripe Docs, “Idempotent requests” (preventing duplicate charges on retry).',
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
    name: 'AZOA note: A retried step should settle once, or not at all.',
    urls: ['https://azoa.ardanova.com/notes/settle-once-or-not-at-all'],
    datePublished: '2026-11-09',
  },
  {
    name: 'PrimusNeo note: Paid twice, or never.',
    urls: ['https://primusneo.com/blog/paid-twice-or-never'],
  },
]

const failClosedSources: readonly WorkflowSource[] = [
  {
    name: 'Saltzer & Schroeder, “The Protection of Information in Computer Systems,” 1975.',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/Basic.html'],
    datePublished: '1975',
  },
  {
    name: 'Saltzer & Schroeder manuscript front matter, 1975.',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/Meta.html'],
    datePublished: '1975',
  },
  {
    name: 'AZOA note: Fail closed, or it isn’t a control.',
    urls: ['https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control'],
    datePublished: '2026-11-09',
  },
]

const consentSources: readonly WorkflowSource[] = [
  {
    name: 'Regulation (EU) 2016/679, Article 7 (Conditions for consent).',
    urls: [articleSeven],
  },
  {
    name: 'AZOA Architecture & Privacy documentation.',
    urls: [azoaArchitecture, 'https://azoa.ardanova.com/privacy'],
  },
]

export const WORKFLOWS: readonly WorkflowPage[] = [
  {
    slug: 'settle-once',
    title: 'Settle once',
    h1: 'A retried step should settle once, or not at all.',
    seoTitle: 'Settle once — Workflows | AZOA',
    description:
      'How AZOA ensures a payout, grant release, or transfer settles once or not at all — even when a connection drops mid-way.',
    eyebrow: 'RELIABLE MONEY & AGREEMENTS · SETTLE ONCE',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'When a payout goes out and the screen hangs, nobody knows whether the money moved. AZOA gives every step a lasting memory so a retry resolves to the exact same answer instead of paying someone twice.',
    sources: settleOnceSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'Two sensible people looking at a frozen confirmation screen tend to react in opposite ways: one clicks Send again thinking nothing happened; the other assumes it went through and closes the ticket. Without a shared record tying retries to the original attempt, ordinary network hiccups turn into double payments or unpaid work.',
        ],
      },
      { type: 'h2', text: 'One request, one outcome' },
      {
        type: 'p',
        runs: [
          'Payment networks like ',
          { strong: 'Stripe' },
          ' and real-time clearing systems like ',
          { strong: 'FedNow' },
          ' solve this by attaching a unique identifier to every action. If a reply gets lost and the same step is submitted again, the receiving system returns the first outcome rather than running the transfer a second time.',
        ],
      },
      { type: 'h2', text: 'How AZOA does it' },
      {
        type: 'p',
        runs: [
          'Every step in an AZOA action graph remembers its own unique key from the moment it starts. Whether a workflow settles across digital networks or a traditional payment partner, AZOA records what happened once and refuses to send the same instruction twice.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Suppose a community fund approves a milestone payout right as a connection drops. Clicking submit a second time—or resuming after a server restart—finds the original payout attempt and shows its real state instead of issuing a second grant. Read more in ',
          { text: 'Paid twice, or never', href: 'https://primusneo.com/blog/paid-twice-or-never' },
          '.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Check reality before retrying', href: '/workflows/reconciliation-over-blind-retry' },
            ' — confirm what actually happened before trying again.',
          ],
          [
            { text: 'Safe retries across networks', href: '/workflows/idempotent-requests' },
            ' — keep everyday retries from multiplying side effects.',
          ],
          [
            { text: 'Clean recovery when plans change', href: '/workflows/compensating-transactions' },
            ' — unwind partial work neatly when a later step cannot finish.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'reconciliation-over-blind-retry',
    title: 'Check reality before retrying',
    h1: 'Reconcile, never guess.',
    seoTitle: 'Reconcile, never guess — Workflows | AZOA',
    description:
      'Why AZOA pauses uncertain steps and checks the real world before moving forward, instead of blindly retrying on a timer.',
    eyebrow: 'RELIABLE MONEY & AGREEMENTS · RECONCILIATION',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'When an outside service fails to answer in time, automatically firing the same request over and over on a timer can create the very duplicate transfer you wanted to avoid.',
    sources: [
      {
        name: 'AZOA Architecture & open-source repository.',
        urls: [azoaArchitecture, azoaRepo],
        datePublished: UPDATED,
      },
      {
        name: 'Federal Reserve Banks, FedNow Service Operating Procedures v3.2 (checking status before resending).',
        urls: ['https://www.frbservices.org/wp-content/uploads/062425-fednow-service-operating-procedures.pdf'],
        datePublished: '2025-06',
      },
      {
        name: 'Stripe Docs, “Idempotent requests”.',
        urls: ['https://docs.stripe.com/api/idempotent_requests'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Most background job queues treat every timeout as a reason to try again in thirty seconds. That works fine for refreshing a web page, but for payouts, escrow releases, or real obligations, guessing can double-spend or corrupt the record.',
        ],
      },
      { type: 'h2', text: 'Ask the source of truth first' },
      {
        type: 'p',
        runs: [
          'When an external partner goes silent mid-step, AZOA pauses the workflow in a clear waiting state instead of looping blindly. Before anything moves again, the engine checks the real source of truth at the destination:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Confirmed:' },
            ' The original step actually went through, so AZOA marks it complete and moves forward without sending it again.',
          ],
          [
            { strong: 'Confirmed failure:' },
            ' The destination proves the original step never took effect, so AZOA can safely retry or cleanly reverse earlier holds.',
          ],
          [
            { strong: 'Still unclear:' },
            ' The answer is genuinely ambiguous, so AZOA keeps the step paused and hands it to a human operator with the full context rather than guessing.',
          ],
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Imagine an end-of-month supplier run where a banking partner slows down and stops confirming responses for ten minutes. Rather than hammering the partner with repeated transfers, AZOA holds each unconfirmed step, checks which ones landed once the partner recovers, and picks up cleanly from verified facts.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — give every attempt a memory so duplicates never land.',
          ],
          [
            { text: 'Waiting patiently for real life', href: '/workflows/durable-waits-and-signals' },
            ' — pause across hours or days without losing track of a run.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — refuse to advance whenever proof is missing.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'fail-closed',
    title: 'Fail-closed safeguards',
    h1: 'Anything that cannot be verified is refused — not waved through.',
    seoTitle: 'Fail-closed safeguards — Workflows | AZOA',
    description:
      'Why AZOA refuses unverified approvals, missing consent, and broken checks outright instead of letting workflows slip past with a quiet warning.',
    eyebrow: 'TRUST, WAITS & CLARITY · FAIL CLOSED',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Software is often tuned to keep progress bars moving even when checks fail. A safeguard that writes a warning to a log file and waves an unverified action through is not a safeguard — it is an open door.',
    sources: failClosedSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'When a confirmation is missing, an identity check is incomplete, or someone withdrew permission, letting the process continue anyway hides the mistake until someone has to untangle it weeks later.',
        ],
      },
      { type: 'h2', text: 'Default to safe refusal' },
      {
        type: 'p',
        runs: [
          'Security pioneers Jerome Saltzer and Michael Schroeder named this principle ',
          { strong: 'fail-safe defaults' },
          ': a system should require explicit proof before allowing action, never assume permission because nothing objected. A noisy refusal happens immediately while everyone can still see why; a quiet mistake slips into production unseen.',
        ],
      },
      { type: 'h2', text: 'How AZOA applies it' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Stop at missing proof:' },
            ' If a required sign-off, disclosure, or consent check cannot be confirmed, the workflow stops cleanly and explains why.',
          ],
          [
            { strong: 'No silent fallback:' },
            ' If terms changed since a participant reviewed them, AZOA refuses to proceed on outdated assumptions.',
          ],
          [
            { strong: 'Human escalation when needed:' },
            ' When reality itself is ambiguous, an operator sees the whole picture rather than software pretending to know.',
          ],
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A grant program requires two reviewer approvals before releasing funds. If one reviewer revoked approval right before disbursement, or the verification service went offline, AZOA stops at the gate rather than sending money first and emailing an alert afterward.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Lock terms before approval', href: '/workflows/version-pinned-execution' },
            ' — make sure nobody approves a moving target.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — ensure unconfirmed steps never repeat blindly.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — stop downstream action as soon as consent ends.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'version-pinned-execution',
    title: 'Lock terms before approval',
    h1: 'Make sure what you approve is exactly what runs.',
    seoTitle: 'Lock terms before approval — Workflows | AZOA',
    description:
      'How AZOA locks the exact version of an agreement before anyone approves it, preventing silent changes between review and confirmation.',
    eyebrow: 'TRUST, WAITS & CLARITY · VERSION SAFETY',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'Imagine opening an agreement to release $500, stepping away for two minutes to double-check an invoice, and clicking Approve — unaware that someone edited the draft to $5,000 while your browser tab sat open.',
    sources: [
      {
        name: 'AZOA Architecture & open-source repository.',
        urls: [azoaArchitecture, azoaRepo],
        datePublished: UPDATED,
      },
      {
        name: 'Saltzer & Schroeder, “The Protection of Information in Computer Systems” (Complete mediation), 1975.',
        urls: ['https://web.mit.edu/saltzer/www/publications/protection/Basic.html'],
        datePublished: '1975',
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Whenever two steps happen at different times — reading an agreement and signing off on it — an unpinned workflow can accidentally bind your approval to a newer edit you never read.',
        ],
      },
      { type: 'h2', text: 'Preview and lock in two clear steps' },
      {
        type: 'p',
        runs: [
          'AZOA separates showing what a workflow will do from actually starting it. When someone opens a workflow to review, AZOA shows a clear summary of every value-moving step alongside a fingerprint of that exact published version.',
        ],
      },
      {
        type: 'p',
        runs: [
          'When they click approve, AZOA checks that the fingerprint still matches. If an organizer edited, unpublished, or replaced the workflow while they were reading, AZOA refuses to start, refreshes the updated terms, and asks for fresh confirmation.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'An organizer reviews a twelve-person grant cohort and clicks start just as a colleague edits one recipient amount. Because the locked version no longer matches, AZOA pauses immediately and surfaces the updated summary instead of quietly paying the wrong amount.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — treat mismatches as an automatic stop.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — tie every approval to the exact terms agreed.',
          ],
          [
            { text: 'Proof of permission', href: '/workflows/demonstrable-consent' },
            ' — preserve clear evidence of what was approved.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'durable-waits-and-signals',
    title: 'Waiting patiently for real life',
    h1: 'Workflows that wait for people, deadlines, and the real world.',
    seoTitle: 'Waiting patiently for real life — Workflows | AZOA',
    description:
      'How AZOA lets approvals, grants, and agreements pause for hours or weeks without losing state across restarts or updates.',
    eyebrow: 'TRUST, WAITS & CLARITY · DURABLE WAITS',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'Real organizations do not run in milliseconds. Approvals take days, cooling-off periods take weeks, and deliveries arrive whenever trucks show up. AZOA workflows park patiently and resume right where they left off.',
    sources: [
      {
        name: 'AZOA Architecture & open-source repository.',
        urls: [azoaArchitecture, azoaRepo],
        datePublished: UPDATED,
      },
      {
        name: 'Hector Garcia-Molina and Kenneth Salem, “Sagas,” Princeton University.',
        urls: ['https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf'],
      },
      {
        name: 'OMG Business Process Model and Notation (BPMN) Version 2.0.2.',
        urls: ['https://www.omg.org/spec/BPMN/2.0.2/PDF'],
        datePublished: '2013-12',
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Traditional apps keep workflows alive inside temporary memory or fragile background cron scripts. When a server restarts or deploys a new version, half-finished processes get stranded or fire twice.',
        ],
      },
      { type: 'h2', text: 'Four clear reasons to wait' },
      {
        type: 'p',
        runs: [
          'Every pause in an AZOA action graph is stored durably with the exact reason it is waiting:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Waiting on a person:' },
            ' Paused until a reviewer, manager, or partner explicitly steps it forward.',
          ],
          [
            { strong: 'Waiting on an outside event:' },
            ' Parked at a gate until a delivery confirmation, signature, or external signal arrives.',
          ],
          [
            { strong: 'Waiting on a schedule:' },
            ' Held until a cooling-off window, milestone deadline, or release date passes.',
          ],
          [
            { strong: 'Waiting on verification:' },
            ' Frozen after an unclear answer until the engine confirms what happened at the destination.',
          ],
        ],
      },
      { type: 'h2', text: 'No stale clicks or skipped steps' },
      {
        type: 'p',
        runs: [
          'Every resume points at the specific gate that was waiting. If someone clicks an old approval link after a workflow already moved forward or was cancelled, AZOA ignores the stale signal cleanly so a process can never jump ahead twice.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A supplier contract requires a three-day review period followed by a warehouse inspection sign-off. Even if the system restarts several times over those three days, the workflow remembers every prior result and wakes cleanly the moment the inspection is logged.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Check reality before retrying', href: '/workflows/reconciliation-over-blind-retry' },
            ' — resolve unclear outcomes before moving on.',
          ],
          [
            { text: 'Clean recovery when plans change', href: '/workflows/compensating-transactions' },
            ' — handle rejections smoothly after a long wait.',
          ],
          [
            { text: 'Shared visual workflows', href: '/workflows/workflow-state-machines' },
            ' — map out every wait and branch together.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'consent-records',
    title: 'Consent records',
    h1: 'Apps act for you only with permission you can see and revoke.',
    seoTitle: 'Consent records — Workflows | AZOA',
    description:
      'Why AZOA keeps permission as a living, visible record that you own — never a forgotten checkbox buried at signup.',
    eyebrow: 'IDENTITY & PERMISSION · CONSENT RECORDS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Most platforms treat consent like a one-time checkbox you click when signing up and never see again. In AZOA, your identity belongs to you, and any app acting for you holds permission you can inspect and revoke at any moment.',
    sources: consentSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'Real-world coordination involves multiple people and apps working together on your behalf — sending receipts, claiming credits, releasing milestones. If permissions are hidden or permanent, users lose control of their own economic lives.',
        ],
      },
      { type: 'h2', text: 'Living permission built into every step' },
      {
        type: 'p',
        runs: [
          'AZOA models consent as a clear agreement between you and an app: who gave permission, who received it, exactly what actions it covers, and when it ends. Before a workflow takes a delegated step, it checks that your permission is still active right now — not weeks ago at signup.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A contributor lets a workspace coordinate milestone payouts and share their public profile during a project. Two weeks later they turn sharing off; future steps stop sharing automatically while keeping past approved receipts accurate.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — turn permissions off immediately with zero friction.',
          ],
          [
            { text: 'Proof of permission', href: '/workflows/demonstrable-consent' },
            ' — keep lasting clarity on what was agreed.',
          ],
          [
            { text: 'Clear, separate choices', href: '/workflows/distinguishable-consent' },
            ' — avoid bundling unrelated approvals together.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'idempotent-requests',
    title: 'Safe retries across networks',
    h1: 'Safe retries in an imperfect network',
    seoTitle: 'Safe retries across networks — Workflows | AZOA',
    description:
      'Why retrying a dropped request should never duplicate real-world work, and how AZOA separates safe repeats from risky guesswork.',
    eyebrow: 'RELIABLE MONEY & AGREEMENTS · SAFE RETRIES',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Mobile connections drop, Wi-Fi flickers, and proxies hiccup right after a server finishes your request. Knowing when a repeat is harmless — and when it needs a shared key — keeps everyday life from duplicating real work.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, June 2022, §9.2.2 (Idempotent Methods).',
        urls: [rfc9110],
        datePublished: '2022-06',
      },
      {
        name: 'PrimusNeo guide: Idempotent requests.',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
      },
      {
        name: 'AZOA guide: Settle once.',
        urls: ['/workflows/settle-once'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'When you tap Confirm on a train and lose signal, your phone never receives the confirmation screen even though the system already recorded your request. If your browser or app blindly tries again without a shared reference, one click turns into two separate actions.',
        ],
      },
      { type: 'h2', text: 'Why web standards separate reading from creating' },
      {
        type: 'p',
        runs: [
          'Under web standards (',
          { strong: 'RFC 9110' },
          '), checking status is safe to repeat automatically, and setting a specific record once is repeatable when properly keyed — whereas firing a brand-new action blindly is never safe to repeat on its own. AZOA pairs every workflow action with a saved caller key before it leaves your device so even after a page reload, retrying converges to the single original answer.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A field coordinator approves an equipment reimbursement on weak cellular signal, refreshes the browser when the spinner stalls, and hits submit again. Because the browser saved the same request key before sending, AZOA recognizes the original attempt and shows the completed status instead of paying twice. Read more at ',
          { text: 'PrimusNeo', href: 'https://primusneo.com/guides/idempotent-requests' },
          '.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — make every retried step settle once or not at all.',
          ],
          [
            { text: 'Reading safely vs. taking action', href: '/workflows/safe-and-idempotent' },
            ' — never attach side effects to simple page views.',
          ],
          [
            { text: 'Check reality before retrying', href: '/workflows/reconciliation-over-blind-retry' },
            ' — pause and verify whenever an outcome is uncertain.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'consent-withdrawal',
    title: 'Withdrawing consent',
    h1: 'Saying no should be as easy as saying yes.',
    seoTitle: 'Withdrawing consent — Workflows | AZOA',
    description:
      'Why AZOA makes withdrawing consent immediate and effortless, stopping upcoming steps right away without erasing honest history.',
    eyebrow: 'IDENTITY & PERMISSION · REVOCATION',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Too many products let you say yes with one tap, then hide the off switch behind three settings menus and a support ticket. In AZOA, withdrawing permission takes effect right away.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7, UK version.',
        urls: [articleSeven],
      },
      {
        name: 'AZOA guide: Consent records.',
        urls: ['/workflows/consent-records'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Permission that is painful to take back is not genuine permission. And if an engine forgets to check whether permission is still active before running a later step, clicking revoke does nothing until damage is already done.',
        ],
      },
      { type: 'h2', text: 'No hidden mazes, no delayed cutoff' },
      {
        type: 'p',
        runs: [
          'European privacy rules (Article 7) put it in one plain sentence: ',
          { strong: 'it shall be as easy to withdraw as to give consent' },
          '. In AZOA, withdrawing an app’s permission happens directly where grants live, and every upcoming workflow step checks live permission before acting — while keeping past authorized history intact.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A creator joins a new app using their AZOA identity and later decides to move to another zone. With one click they withdraw the first app’s permission; their identity and history stay theirs, and the old app loses access immediately.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — visible, revocable agreements owned by the user.',
          ],
          [
            { text: 'Proof of permission', href: '/workflows/demonstrable-consent' },
            ' — track both when consent started and when it ended.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — block future steps automatically when consent is gone.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'least-privilege',
    title: 'Only the access needed',
    h1: 'Least privilege: only the access the job requires',
    seoTitle: 'Least privilege — Workflows | AZOA',
    description:
      'Why AZOA limits every app, credential, and workflow step strictly to the narrow permissions needed for its specific task.',
    eyebrow: 'IDENTITY & PERMISSION · SCOPED ACCESS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Handing an integration full master access because "it’s quicker to set up" puts everything else at risk. AZOA scopes permissions so every app and automated step gets only what its specific job requires.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AC-6 Least Privilege.',
        urls: [nist80053, nist80053Doi, nist80053Csrc],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'In complex organizations, one workflow might verify a receipt, read an address, and settle a payout across separate systems. If the tool sending notifications also carries treasury privileges, a tiny bug in a message template becomes a major security hazard.',
        ],
      },
      { type: 'h2', text: 'Narrow permissions for people and automated steps alike' },
      {
        type: 'p',
        runs: [
          'Federal security guidelines (',
          { strong: 'NIST SP 800-53' },
          ') apply least privilege equally to humans and to software acting for them. AZOA issues short-lived, scope-limited permissions so an app built to read milestone status can never unexpectedly sign a transfer or change account settings.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A community newsletter app only needs permission to read opted-in member updates, never to initiate payouts. Even if that app is updated or compromised later, its narrow scope stops it from touching funds.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
            ' — keep proposing and approving in separate hands.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — refuse any action outside the granted scope.',
          ],
          [
            { text: 'An honest audit trail', href: '/workflows/event-logging' },
            ' — see clearly which scoped permission acted at each step.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'separation-of-duties',
    title: 'Separation of duties',
    h1: 'Separation of duties',
    seoTitle: 'Separation of duties — Workflows | AZOA',
    description:
      'Why high-stakes workflows divide authoring, execution, and final approval across distinct people instead of letting one account hold all keys.',
    eyebrow: 'IDENTITY & PERMISSION · SHARED CHECKS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'If the same single account can draft a payout rule, publish it live, approve its human checkpoint, and override an ambiguous error, approvals are just theater.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AC-5 Separation of Duties.',
        urls: [nist80053, nist80053Doi],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Most costly mistakes inside organizations are not exotic hacks — they happen when one hurried person can both request a change and approve it with zero independent eyes.',
        ],
      },
      { type: 'h2', text: 'Separate roles built into the graph' },
      {
        type: 'p',
        runs: [
          'Security guidance (',
          { strong: 'NIST AC-5' },
          ') starts by naming which duties must stay apart and enforcing that split in permissions. In AZOA, one role designs and publishes a workflow, another runs it with explicit user consent, and a dedicated operator role handles rare human escalations separately from everyday members.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A team sets up a supplier settlement flow where the person who edits workflow templates cannot also sign off as the finance reviewer at the release gate — catching honest typos before funds ever leave.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Only the access needed', href: '/workflows/least-privilege' },
            ' — match each role strictly to its responsibilities.',
          ],
          [
            { text: 'Lock terms before approval', href: '/workflows/version-pinned-execution' },
            ' — ensure reviewers verify the exact template running.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — block self-approval attempts automatically.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'event-logging',
    title: 'An honest audit trail',
    h1: 'Decide what belongs in the record, and who relies on it',
    seoTitle: 'An honest audit trail — Workflows | AZOA',
    description:
      'Why AZOA records clear, meaningful workflow decisions — consents, refusals, handoffs, and settlements — instead of burying teams in debug noise.',
    eyebrow: 'TRUST, WAITS & CLARITY · AUDIT TRAIL',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Teams often say "we log everything" until someone asks why a payout paused three weeks ago — and finds gigabytes of chatter with zero clear history of who approved or cancelled the run.',
    sources: [
      {
        name: 'NIST SP 800-53 Revision 5, AU-2 Event Logging.',
        urls: [nist80053, nist80053Doi],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'An audit trail only helps people if it captures the real turning points of an agreement cleanly: who consented, what rules were locked in, where a check refused or waited, and how an operator resolved an ambiguous case.',
        ],
      },
      { type: 'h2', text: 'Meaningful milestones over noisy logs' },
      {
        type: 'p',
        runs: [
          'Audit guidance (',
          { strong: 'NIST AU-2' },
          ') emphasizes deciding upfront which events matter and coordinating them with the finance, compliance, and operations teams who actually read them. AZOA turns every action graph transition into a clean, inspectable history rather than forcing teams to piece together scattered spreadsheets and server logs afterward.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Three months after a grant round closes, an auditor asks whether a milestone waited for manual sign-off or released automatically on a timer. AZOA shows the exact timeline, consent grant, and step-by-step history in one place.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — keep grants and withdrawals in the permanent record.',
          ],
          [
            { text: 'Waiting patiently for real life', href: '/workflows/durable-waits-and-signals' },
            ' — preserve complete context across multi-day pauses.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — record clear reasons whenever a step stops.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'workflow-state-machines',
    title: 'Shared visual workflows',
    h1: 'One shared map for operators, builders, and communities',
    seoTitle: 'Shared visual workflows — Workflows | AZOA',
    description:
      'How drawing agreements and processes as visual action graphs keeps business owners, reviewers, and engineers aligned before anything runs.',
    eyebrow: 'TRUST, WAITS & CLARITY · VISUAL GRAPHS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'When an approval process lives scattered across email threads and hidden scripts, operations teams, reviewers, and developers all carry different pictures of what happens when something goes wrong.',
    sources: [
      {
        name: 'OMG BPMN catalog, Version 2.0.2.',
        urls: ['https://www.omg.org/spec/BPMN'],
        datePublished: '2014-01',
      },
      {
        name: 'Business Process Model and Notation (BPMN) Version 2.0.2.',
        urls: ['https://www.omg.org/spec/BPMN/2.0.2/PDF'],
        datePublished: '2013-12',
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Before automating payouts, supplier milestones, or circular credits, everyone involved needs one clear picture of happy paths, waiting periods, failure exits, and human checkpoints.',
        ],
      },
      { type: 'h2', text: 'From visual graph to living workflow' },
      {
        type: 'p',
        runs: [
          'Standards like ',
          { strong: 'BPMN' },
          ' showed the value of a common visual language connecting process designers and implementers. In AZOA, you build actions visually as connected steps — gates, timers, transfers, and reviews — validate the graph upfront, and run the exact same map you drew.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A co-op board wants to know what happens if a delivery arrives damaged after a deposit was held. Looking at the visual action graph together makes the inspection gate, hold release, and human review branch obvious before a single transaction runs.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Clean recovery when plans change', href: '/workflows/compensating-transactions' },
            ' — make failure and refund branches explicit on the graph.',
          ],
          [
            { text: 'Waiting patiently for real life', href: '/workflows/durable-waits-and-signals' },
            ' — see how waits and deadlines fit into the flow.',
          ],
          [
            { text: 'Lock terms before approval', href: '/workflows/version-pinned-execution' },
            ' — publish validated graphs so everyone signs off on the same map.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'compensating-transactions',
    title: 'Clean recovery when plans change',
    h1: 'Clean recovery for multi-step commitments',
    seoTitle: 'Clean recovery when plans change — Workflows | AZOA',
    description:
      'How long-running workflows unwind partial agreements cleanly when a late step cannot succeed, instead of leaving funds stranded.',
    eyebrow: 'RELIABLE MONEY & AGREEMENTS · RECOVERY',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'When a process spans days and multiple partners, you cannot freeze everything under one giant lock. If step three fails after steps one and two succeeded, you need a clean, pre-planned way to make things right.',
    sources: [
      {
        name: 'Hector Garcia-Molina and Kenneth Salem, “Sagas,” Princeton University.',
        urls: ['https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'In real life, once an escrow reserve is locked or a circular credit is issued, you cannot pretend it never happened. Clean recovery means recording an explicit next step — releasing the reserve, issuing a refund, or returning the credit — so both sides see a complete, honest ledger.',
        ],
      },
      { type: 'h2', text: 'Finish together, or unwind cleanly' },
      {
        type: 'p',
        runs: [
          'Computer scientists Hector Garcia-Molina and Kenneth Salem named this pattern ',
          { em: 'Sagas' },
          ': break a long process into clear steps where either every step finishes, or explicit ',
          { strong: 'compensating actions' },
          ' undo earlier steps cleanly in reverse. AZOA uses this pattern so partial failures cascade safely instead of leaving an operator digging through broken records.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A grant workflow reserves funds in step one, verifies milestone paperwork in step two, and fails an independent quality check in step three. AZOA automatically triggers the release of the reserved funds back to the pool and records the whole story clearly.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Check reality before retrying', href: '/workflows/reconciliation-over-blind-retry' },
            ' — confirm a step truly failed before unwinding earlier ones.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — ensure refunds and releases happen once too.',
          ],
          [
            { text: 'Waiting patiently for real life', href: '/workflows/durable-waits-and-signals' },
            ' — wait cleanly between steps without holding systems hostage.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'safe-and-idempotent',
    title: 'Reading safely vs. taking action',
    h1: 'Viewing a page should never trigger a payment or deletion',
    seoTitle: 'Reading safely vs. taking action — Workflows | AZOA',
    description:
      'Why viewing a link or previewing status must never change state — protecting people from accidental approvals by email scanners or page reloads.',
    eyebrow: 'RELIABLE MONEY & AGREEMENTS · SAFE INTERACTIONS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Viewing a receipt or checking an approval link should never accidentally trigger an irreversible action. Keeping inspection strictly separate from taking action protects users from stray clicks and automated link scanners.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, §9.2.1 (Safe Methods) and §9.2.2 (Idempotent Methods).',
        urls: [rfc9110],
        datePublished: '2022-06',
      },
      {
        name: 'AZOA guide: Safe retries across networks.',
        urls: ['/workflows/idempotent-requests'],
      },
      {
        name: 'PrimusNeo guide: Idempotent requests.',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Email security tools, link previewers, and browser prefetchers routinely open links inside messages before a human ever sees them. If an approval or delete button is wired up as a plain link, a security scanner can accidentally approve a transfer or delete a draft before you even open your inbox.',
        ],
      },
      { type: 'h2', text: 'Look freely; confirm intentionally' },
      {
        type: 'p',
        runs: [
          'Web standards (',
          { strong: 'RFC 9110' },
          ') draw a bright line between read-only views — which must never change anything — and intentional actions that require deliberate submission. In AZOA, inspecting a workflow, previewing terms, or viewing status never alters balances or state; moving a step forward always requires an explicit, authenticated confirmation.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'When an organizer receives an invitation or milestone link by email, opening it only displays the clear terms and asks for an explicit button press. No link previewer or accidental page refresh can advance the agreement behind their back.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Safe retries across networks', href: '/workflows/idempotent-requests' },
            ' — make intentional submissions safe against dropped connections.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — ensure confirmed actions apply only once.',
          ],
          [
            { text: 'Fail-closed safeguards', href: '/workflows/fail-closed' },
            ' — block unconfirmed transitions automatically.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'demonstrable-consent',
    title: 'Proof of permission',
    h1: 'Permission that can be shown, not just assumed',
    seoTitle: 'Proof of permission — Workflows | AZOA',
    description:
      'Why AZOA keeps clear, verifiable proof of who granted permission, for what purpose, and when — instead of overwriting a silent yes/no flag.',
    eyebrow: 'IDENTITY & PERMISSION · PROOF OF CONSENT',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Overwriting a single "accepted" checkbox in a database wipes out what someone actually agreed to the moment rules change. Real accountability means being able to show clear proof of permission later.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7(1) (Demonstrating consent).',
        urls: [articleSeven],
      },
      {
        name: 'AZOA guides: Consent records & Lock terms before approval.',
        urls: ['/workflows/consent-records', '/workflows/version-pinned-execution'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'When an app moves funds, shares contributor information, or acts on a member’s behalf, saying "they must have clicked OK at signup" is not good enough for trust or compliance.',
        ],
      },
      { type: 'h2', text: 'Keep the record intact' },
      {
        type: 'p',
        runs: [
          'Privacy law (',
          { strong: 'Article 7(1)' },
          ') requires that whoever relies on consent must be able to demonstrate it. AZOA keeps structured grant records showing who gave permission, who received it, which exact permissions were included, and when they were granted or withdrawn.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'If a member asks why an app had access to trigger a payout last month, AZOA points to the exact dated consent record they approved — and proves immediately when they turned it off afterward.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — tie live grants directly to every delegated action.',
          ],
          [
            { text: 'Clear, separate choices', href: '/workflows/distinguishable-consent' },
            ' — make every permission clear before it is recorded.',
          ],
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — record withdrawals cleanly without rewriting history.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'distinguishable-consent',
    title: 'Clear, separate choices',
    h1: 'Permissions should be clear, plain, and separate from fine print',
    seoTitle: 'Clear, separate choices — Workflows | AZOA',
    description:
      'Why AZOA keeps distinct permissions plain and separate rather than hiding data sharing or payouts inside giant terms checkboxes.',
    eyebrow: 'IDENTITY & PERMISSION · CLARITY',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Burying an important permission inside a 4,000-word terms-of-service page does not earn genuine trust. People deserve plain-language choices kept separate from unrelated boilerplate.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7(2) and 7(4) (Clear and unbundled consent).',
        urls: [articleSeven],
      },
      {
        name: 'AZOA guide: Proof of permission.',
        urls: ['/workflows/demonstrable-consent'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'When everything from directory visibility to financial permissions is lumped into one giant "I agree" button, users have no real choice — and organizations never know what people truly meant to authorize.',
        ],
      },
      { type: 'h2', text: 'One clear toggle per purpose' },
      {
        type: 'p',
        runs: [
          'Under ',
          { strong: 'Article 7(2)' },
          ', any consent bundled inside other written matters must be clearly distinguishable, easy to access, and written in plain language — or it is not binding. AZOA separates optional choices (like whether others can find you in member search) from core permissions so you always choose deliberately.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A creator joining a workspace sees separate, readable controls for account sign-in, search visibility, and specific payout permissions — instead of signing away everything under a single hidden checkbox.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Proof of permission', href: '/workflows/demonstrable-consent' },
            ' — capture clear records for each distinct choice.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — check only the specific permission each step requires.',
          ],
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — change your mind on any single choice anytime.',
          ],
        ],
      },
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
