/** Engineering workflow pattern library and index for /workflows and /workflows/[slug]. */

export const SIMULATION_SENTENCE =
  'Hosted demo runs simulated payloads. Real-value bridging and fiat settlement are disabled; self-host Apache-2.0 source for production workloads.'

const PUBLISHED = '2026-11-10'
const UPDATED = '2026-11-14'

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
const azoaSdkTypes = `${azoaRepo}/blob/main/sdk/azoa-wallet/src/workflow/types.ts`
const azoaSdkClient = `${azoaRepo}/blob/main/sdk/azoa-wallet/src/workflow/client.ts`

export const WORKFLOW_INDEX = {
  eyebrow: 'WORKFLOW PATTERNS · 16 ENGINEERING NOTES',
  h1: 'Workflow guards that hold under failure.',
  seoTitle: 'Workflows | AZOA',
  description:
    'Architectural field notes grouped by execution guarantee: idempotency keys and sagas, consent and access boundaries, audit slices, reconciliation sweeps, and fail-closed version checks.',
  publishedAt: PUBLISHED,
  lastReviewed: UPDATED,
  lede:
    'Multi-step business workflows rarely fail cleanly. Networks drop between a write and its response, two operators retry the same approval after a timeout, or an admin edits a rule while a participant is reading a confirmation screen. These sixteen notes group AZOA action-graph guarantees by failure mode — idempotency & sagas, consent & access control, and audit & fail-closed guards — with primary specifications cited at the bottom of each note.',
  blocks: [
    { type: 'h2', text: 'Idempotency & Sagas' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Settle once', href: '/workflows/settle-once', strong: true },
          ' — turn network timeouts into a settled yes or no using saved idempotency records and status inquiries instead of blind resends.',
        ],
        [
          { text: 'Reconcile indeterminate runs instead of retrying blindly', href: '/workflows/reconciliation-over-blind-retry', strong: true },
          ' — why AZOA parks ambiguous external calls in `AwaitingReconciliation` and relies on targeted probes rather than timer loops.',
        ],
        [
          { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests', strong: true },
          ' — when automatic retries preserve a single side effect and why non-idempotent POST steps require explicit caller keys.',
        ],
        [
          { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent', strong: true },
          ' — distinguish read-only inspection endpoints from mutating calls that are merely safe to repeat.',
        ],
        [
          { text: 'Sagas and compensating transactions', href: '/workflows/compensating-transactions', strong: true },
          ' — unwind partial multi-step executions using explicit backward transactions rather than post-hoc cleanup guesses.',
        ],
      ],
    },
    { type: 'h2', text: 'Consent & Access Control' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Consent records', href: '/workflows/consent-records', strong: true },
          ' — persist who authorized an action, under which terms, and honor mid-flight withdrawal on downstream steps.',
        ],
        [
          { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal', strong: true },
          ' — build revocation paths that require no more friction than the original opt-in.',
        ],
        [
          { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent', strong: true },
          ' — model consent grants as queryable audit artifacts rather than unlogged frontend flags.',
        ],
        [
          { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent', strong: true },
          ' — separate optional workflow permissions cleanly from unrelated contract boilerplate.',
        ],
        [
          { text: 'Least privilege', href: '/workflows/least-privilege', strong: true },
          ' — scope worker and delegated actor credentials strictly to the minimal permission set required by the current step.',
        ],
        [
          { text: 'Separation of duties', href: '/workflows/separation-of-duties', strong: true },
          ' — prevent single actors from both authoring and approving irreversible settlement transitions.',
        ],
      ],
    },
    { type: 'h2', text: 'Audit & Fail-Closed Guards' },
    {
      type: 'ul',
      items: [
        [
          { text: 'Fail-closed controls', href: '/workflows/fail-closed', strong: true },
          ' — reject missing verifications explicitly; logging a warning and continuing is never a control.',
        ],
        [
          { text: 'Pin the published version hash before execution', href: '/workflows/version-pinned-execution', strong: true },
          ' — stop time-of-check to time-of-use drift between economic disclosure previews and run start.',
        ],
        [
          { text: 'Durable waits, external signals, and stale threads', href: '/workflows/durable-waits-and-signals', strong: true },
          ' — park runs cleanly in `Suspended`, `AwaitingSignal`, and `AwaitingTimer` across process restarts without holding memory locks.',
        ],
        [
          { text: 'Event logging', href: '/workflows/event-logging', strong: true },
          ' — decide upfront which state transitions, refusals, and revocations belong in the immutable audit slice.',
        ],
        [
          { text: 'Shared workflow diagrams (BPMN)', href: '/workflows/workflow-state-machines', strong: true },
          ' — give product analysts, operators, and backend engineers one shared diagram before writing code.',
        ],
      ],
    },
    { type: 'h2', text: 'Across the stack' },
    {
      type: 'p',
      runs: [
        'For design essays connecting these patterns, read ',
        { text: 'A retried step should settle once, or not at all', href: '/notes/settle-once-or-not-at-all' },
        ' and ',
        { text: 'Fail closed, or it isn’t a control', href: '/notes/fail-closed-or-it-isnt-a-control' },
        '. To trace how AZOA fits alongside ',
        { text: 'PrimusNeo', href: 'https://primusneo.com' },
        ' and powers shared workspace operations in ',
        { text: 'ArdaNova', href: 'https://ardanova.com' },
        ', see the ',
        { text: 'Architecture overview', href: '/architecture' },
        '.',
      ],
    },
  ] as readonly Block[],
}

const settleOnceSources: readonly WorkflowSource[] = [
  {
    name: 'Stripe Docs, “Idempotent requests” (API v1 and v2 retry semantics).',
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
    name: 'PrimusNeo engineering note: Paid twice, or never.',
    urls: ['https://primusneo.com/blog/paid-twice-or-never'],
  },
]

const failClosedSources: readonly WorkflowSource[] = [
  {
    name: 'Saltzer & Schroeder, “The Protection of Information in Computer Systems,” 1975 (Fail-safe defaults & Complete mediation).',
    urls: ['https://web.mit.edu/saltzer/www/publications/protection/Basic.html'],
    datePublished: '1975',
  },
  {
    name: 'Saltzer & Schroeder manuscript front matter (received 11 October 1974; revised 17 April 1975).',
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
    name: 'Regulation (EU) 2016/679, Article 7 (UK GDPR official text on legislation.gov.uk).',
    urls: [articleSeven],
  },
  {
    name: 'AZOA Architecture & Privacy documentation.',
    urls: ['https://azoa.ardanova.com/architecture', 'https://azoa.ardanova.com/privacy'],
  },
]

export const WORKFLOWS: readonly WorkflowPage[] = [
  {
    slug: 'settle-once',
    title: 'Settle once',
    h1: 'A timeout is not a decision. A retry should not become a second settlement.',
    seoTitle: 'Settle once — Workflows | AZOA',
    description:
      'Prevent duplicate settlement under dropped connections by binding workflow mutations to caller idempotency keys and explicit status inquiries.',
    eyebrow: 'IDEMPOTENCY & SAGAS · SETTLE ONCE',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Network timeouts do not return a "no" — they return silence. Without durable idempotency records and status lookups before resending, one dropped response between client and gateway turns a legitimate retry into a duplicate settlement.',
    sources: settleOnceSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'Two reasonable engineers looking at a hung HTTP payout request often choose opposite failure modes: one retries immediately assuming the call never landed; another suppresses retries assuming it succeeded. Left without a shared key and lookup protocol, the system either pays a supplier twice or leaves both ledgers out of sync while generating plausible logs on either side.',
        ],
      },
      { type: 'h2', text: 'How mature payment systems enforce settle-once behavior' },
      {
        type: 'p',
        runs: [
          'Payment APIs solve this at the request boundary rather than relying on callers to guess. In ',
          { strong: 'Stripe API v1' },
          ', the server saves the HTTP status code and response body of the first request made with a given idempotency key once endpoint execution starts — including 500 errors — and replays that stored result on subsequent retries. By contrast, ',
          { strong: 'Stripe API v2' },
          ' prevents repeating already-completed work and retries failed or partially failed sub-operations toward completion, returning an updated state.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Clearing networks pair key uniqueness with explicit status inquiry. Under ',
          { strong: 'FedNow Service Operating Procedures Version 3.2 (June 2025)' },
          ', participants must attach a unique ISO 20022 message identifier on every submission, and duplicates are rejected with ',
          { strong: '`admi.002`' },
          '. When a timeout expires with uncertain delivery, operators query status first via ',
          { strong: '`pacs.028`' },
          ' or the Adhoc Query Tool rather than blindly re-issuing under a new ID — while receivers deduplicate incoming IDs so duplicates are never processed twice.',
        ],
      },
      { type: 'h2', text: 'How AZOA models the guard' },
      {
        type: 'p',
        runs: [
          'AZOA propagates explicit ',
          { strong: '`Idempotency-Key`' },
          ' headers across workflow initiation, manual step advances (`advance`), and external gate signals (`signal`). When an external settlement adapter fails to confirm within its deadline, the step transitions to `AwaitingReconciliation` instead of auto-retrying blindly.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Suppose a milestone disbursement times out at the gateway while two workers pick up the retry backlog. Both submit the original `Idempotency-Key`, receive the initial recorded state rather than spawning a second transfer, and wait for reconciliation to resolve whether the upstream instruction settled or rolled back. See also the sibling engineering note in ',
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
            { text: 'Reconcile indeterminate runs instead of retrying blindly', href: '/workflows/reconciliation-over-blind-retry' },
            ' — probe actual state at the destination before deciding whether to advance or compensate.',
          ],
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — understand HTTP retry boundaries under RFC 9110.',
          ],
          [
            { text: 'Sagas and compensating transactions', href: '/workflows/compensating-transactions' },
            ' — amend partial runs with forward compensation rather than silent mutations.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — stop execution cleanly whenever authority or verification state is missing.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'reconciliation-over-blind-retry',
    title: 'Reconciliation over blind retry',
    h1: 'Reconcile indeterminate runs instead of retrying blindly.',
    seoTitle: 'Reconcile indeterminate runs instead of retrying blindly — Workflows | AZOA',
    description:
      'How AZOA handles timeout uncertainty with an explicit AwaitingReconciliation workflow state and sweep probes that verify destination truth before advancing.',
    eyebrow: 'IDEMPOTENCY & SAGAS · RECONCILIATION',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'When an external API call hangs and your HTTP client throws a socket timeout, firing an immediate exponential-backoff retry is often the worst possible instinct. If the first attempt succeeded silently downstream, an automated retry loop converts ambiguity into duplicate execution.',
    sources: [
      {
        name: 'AZOA SDK WorkflowRunStatus and WorkflowReconciliationResult definitions.',
        urls: [azoaSdkTypes, azoaSdkClient],
        datePublished: UPDATED,
      },
      {
        name: 'Federal Reserve Banks, FedNow Service Operating Procedures v3.2 (pacs.028 inquiry before resend).',
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
          'Most background job queues classify any exception as "retryable with backoff." That works nicely for stateless read fetches or pure database updates inside one transaction. For external state mutations — ledger settlements, webhook deliveries, or credential issuances — throwing away the in-flight correlation context and blindly retrying risks double-execution whenever the external system committed the first request right as the response socket tore.',
        ],
      },
      { type: 'h2', text: 'Making ambiguity a first-class workflow state' },
      {
        type: 'p',
        runs: [
          'In AZOA’s run lifecycle (`WorkflowRunStatus`), a step that stops with an indeterminate outcome does not enter an automated retry queue. Instead, the engine freezes the entire run into `AwaitingReconciliation`, a non-terminal parked status alongside `Suspended`, `AwaitingSignal`, and `AwaitingTimer`.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Once parked in `AwaitingReconciliation`, the workflow cannot advance or trigger duplicate side effects on a blind timer. Recovery flows exclusively through explicit re-probing (`WorkflowClient.reconcileRun`) or an operator reconciliation sweep (`WorkflowReconciliationSweep`), which inspects the original operation ID at the destination:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Confirmed (`reconciledConfirmed`):' },
            ' The original external operation already committed. AZOA marks the step `Succeeded` and resumes downstream steps without re-issuing the request.',
          ],
          [
            { strong: 'Definite failure:' },
            ' The destination proves the original operation failed or never took effect. Only now is the step cleanly released back for safe retry or saga compensation.',
          ],
          [
            { strong: 'Still indeterminate (`stillIndeterminate`):' },
            ' Destination status remains pending or unknown; the run stays parked for the next sweep rather than fabricating a verdict.',
          ],
        ],
      },
      { type: 'h2', text: 'Why status sweeps beat optimistic timers' },
      {
        type: 'p',
        runs: [
          'This design mirrors the inquiry-first discipline of `pacs.028` clearing checks: query the original attempt before ever deciding whether another instruction is valid. Separating status observation (`reconcileRun`) from fresh execution eliminates race conditions during gateway slowdowns and gives operations teams clear visibility into `scanned`, `errors`, and `hardStuck` counts without ever equating zero errors with confirmed settlement.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Suppose a clearing gateway stalls mid-batch and multiple in-flight payout steps hit a socket timeout. Rather than hammering the gateway with blind retries moments later, AZOA parks every ambiguous run in `AwaitingReconciliation`. Once the gateway stabilizes, a sweep checks upstream operation IDs — advancing confirmed transfers cleanly with zero double-settlement while releasing only confirmed failures for retry or compensation.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — pair caller idempotency keys with status verification.',
          ],
          [
            { text: 'Durable waits, external signals, and stale threads', href: '/workflows/durable-waits-and-signals' },
            ' — understand how parked states persist across restarts without open threads.',
          ],
          [
            { text: 'Sagas and compensating transactions', href: '/workflows/compensating-transactions' },
            ' — unwind multi-step runs cleanly once a failure is confirmed.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'fail-closed',
    title: 'Fail-closed controls',
    h1: 'If it cannot be verified, refuse it. A warning is not a control.',
    seoTitle: 'Fail-closed controls — Workflows | AZOA',
    description:
      'Why permission checks must deny execution whenever verification fails or stalls, applying Saltzer and Schroeder’s fail-safe defaults to multi-step action graphs.',
    eyebrow: 'AUDIT & FAIL-CLOSED GUARDS · ACCESS SAFETY',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Distributed workflows tend to optimize for keeping queues moving. When a permission probe times out or a required confirmation hasn’t arrived, teams are tempted to log a warning and advance the step anyway — building a green dashboard over silent policy violations.',
    sources: failClosedSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'A safeguard that silently falls back to "allow" whenever verification degrades is merely telemetry dressed as security. When verification is unreachable, missing, or expired, a fail-closed workflow stops cleanly and records refusal as its explicit outcome.',
        ],
      },
      { type: 'h2', text: 'Fail-safe defaults and complete mediation' },
      {
        type: 'p',
        runs: [
          'In their classic 1975 paper, Jerome Saltzer and Michael Schroeder grounded access safety in ',
          { strong: 'fail-safe defaults' },
          ' (attributed to E. Glaser in 1965): base authorization on explicit permission rather than exclusion, because a false refusal surfaces immediately while a false allowance quietly slips through normal traffic until audited much later.',
        ],
      },
      {
        type: 'p',
        runs: [
          'They paired that rule with ',
          { strong: 'complete mediation' },
          ': every access must be validated against live authority — across normal transitions, retries, recovery sweeps, and shutdown paths — without blindly trusting cached "yes" verdicts after state has moved on.',
        ],
      },
      { type: 'h2', text: 'How AZOA enforces fail-closed transitions' },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Refusal over warning:' },
            ' A gate missing a required signal, signature, or consent flag halts the transition instead of writing a warning line and proceeding.',
          ],
          [
            { strong: 'Hash validation on start:' },
            ' If a published action graph was edited after an actor reviewed its terms, version pinning fails the run start with a conflict error rather than running mutated steps.',
          ],
          [
            { strong: 'Non-caching authority reads:' },
            ' When an upstream read fails or returns unreadable state, UI and worker surfaces report verification inability explicitly rather than assuming cleared funds or active consent.',
          ],
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A grant release step expects a compliance reviewer’s signal before transferring funds. If the reviewer revoked approval five seconds earlier, or the policy service returned HTTP 503 during validation, the AZOA gate refuses to advance — writing an explicit audit refusal rather than firing a Slack alert after the transfer already left.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Pin the published version hash before execution', href: '/workflows/version-pinned-execution' },
            ' — enforce Saltzer & Schroeder completeness between disclosure preview and run start.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — refuse blind retries when settlement status is unconfirmed.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — stop downstream actions automatically once consent is withdrawn.',
          ],
          [
            { text: 'Least privilege', href: '/workflows/least-privilege' },
            ' — restrict each workflow actor exclusively to task-required scopes.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'version-pinned-execution',
    title: 'Version-pinned execution',
    h1: 'Pin the published workflow hash before an actor consents.',
    seoTitle: 'Pin the published workflow hash before execution — Workflows | AZOA',
    description:
      'Prevent time-of-check to time-of-use drift in action graphs by coupling economic disclosure previews with optimistic expectedPublishedHash guards on run start.',
    eyebrow: 'AUDIT & FAIL-CLOSED GUARDS · VERSION PINNING',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'Suppose an approver opens a workflow disclosure showing a $500 recipient payout, steps away to verify an invoice, and clicks Approve two minutes later — unaware that a workspace admin edited the underlying action template to send $5,000 in the meantime. Without cryptographic version pinning, that click executes terms the approver never read.',
    sources: [
      {
        name: 'AZOA SDK previewRun, ActionEconomicManifest, and expectedPublishedHash implementation.',
        urls: [azoaSdkClient, azoaSdkTypes],
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
          'In concurrent multi-party systems, checking permissions or rendering disclosures before an irreversible start creates a classic ',
          { strong: 'time-of-check to time-of-use (TOCTOU)' },
          ' gap. An action definition ID (`actionId`) is a mutable pointer across edits, repointings, and unpublish cycles; only a published content hash pins the exact step graph, recipients, and economic rules a caller consented to.',
        ],
      },
      { type: 'h2', text: 'Two-step disclosure and hash enforcement in AZOA' },
      {
        type: 'p',
        runs: [
          'AZOA’s workflow SDK (`WorkflowClient`) separates preview inspection from execution using two coordinated primitives:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: '1. Pre-flight disclosure (`previewRun`):' },
            ' Before starting execution, the caller fetches an `ActionEconomicManifest` containing every value-bearing step (`entries`), `hasEconomicNodes`, and the exact `publishedVersionHash` of the published graph.',
          ],
          [
            { strong: '2. Version-pinned start (`startWorkflow`):' },
            ' When submitting start options (`StartWorkflowOptions`), the client echoes back `expectedPublishedHash`, `acknowledgeEconomicEffects`, and a persistent `idempotencyKey`.',
          ],
        ],
      },
      {
        type: 'p',
        runs: [
          'If another operator updated, unpublished, or republished the template between preview and start, the server rejects the request cleanly with an action conflict (`400`) instead of silently binding stale consent to mutated steps.',
        ],
      },
      { type: 'h2', text: 'Why version pinning must travel with idempotency' },
      {
        type: 'p',
        runs: [
          'Retrying a failed network response around `startWorkflow` preserves both the original `idempotencyKey` and `expectedPublishedHash`. That guarantees network recovery can never accidentally adopt a newer definition published mid-retry: either the pinned version starts once, or the run fails closed and prompts the caller to review the updated manifest.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'An operator opens a cohort grant run in a browser, inspects the manifest of 12 recipients, and clicks start just as an administrator removes a grantee and republishes the graph. Because `expectedPublishedHash` disagrees with the active published version, AZOA aborts the start before a single step initializes, refreshes the preview, and asks for explicit confirmation against the new manifest.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — make mismatch refusal a hard protocol invariant rather than a UI warning.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — capture who agreed to exactly what disclosure state.',
          ],
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — preserve verifiable evidence linking approval to exact scope.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'durable-waits-and-signals',
    title: 'Durable waits, signals, and stale threads',
    h1: 'Durable waits, external signals, and stale threads.',
    seoTitle: 'Durable waits, external signals, and stale threads — Workflows | AZOA',
    description:
      'Replace fragile in-memory sleepers and long-lived worker locks with durable wait states (Suspended, AwaitingSignal, AwaitingTimer) that survive restarts.',
    eyebrow: 'AUDIT & FAIL-CLOSED GUARDS · DURABLE STATE',
    publishedAt: UPDATED,
    lastReviewed: UPDATED,
    lede:
      'Long-lived workflows often sit idle for hours or days waiting for a human approver, an onboarding webhook, or a cooling-off timer. Holding open HTTP connections or in-memory worker threads during those waits turns every container deploy or restart into lost workflow state.',
    sources: [
      {
        name: 'AZOA SDK WorkflowRunStatus, AdvanceParams, and SignalParams.',
        urls: [azoaSdkTypes, azoaSdkClient],
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
          'Real-world coordination runs on human time, not millisecond RPC latency. If your architecture models an approval pause using an in-memory promise, a background thread `sleep()`, or a polling cron loop guessing whether a step is ready, deployments corrupt state and stale callbacks double-trigger downstream branches.',
        ],
      },
      { type: 'h2', text: 'Four distinct parked states instead of one vague "Waiting"' },
      {
        type: 'p',
        runs: [
          'AZOA persists every run transition in storage so workers hold zero memory between steps. Non-terminal parked states are explicitly typed by what event unlocks them:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: '`Suspended`:' },
            ' Paused between steps waiting on an authorized manual advance (`advance(runId, fromNodeId)`) carrying an explicit step UUID.',
          ],
          [
            { strong: '`AwaitingSignal`:' },
            ' Parked at a named gate waiting for an external webhook or actor to send `signal(runId, gateId, payload)`.',
          ],
          [
            { strong: '`AwaitingTimer`:' },
            ' Parked on a scheduled deadline that wakes cleanly via the saga trigger when the window matures.',
          ],
          [
            { strong: '`AwaitingReconciliation`:' },
            ' Frozen after an indeterminate downstream response so blind retries cannot touch the run until state probing finishes.',
          ],
        ],
      },
      { type: 'h2', text: 'Defeating stale threads with scoped step pointers' },
      {
        type: 'p',
        runs: [
          'A recurring bug in asynchronous orchestrators is the ',
          { strong: 'stale thread' },
          ': a late worker callback or slow user browser submits a resume call targeting step 2 after the run has already moved to step 4 or been `Cancelled`. Because `advance` names `fromNodeId`, `signal` names `gateId`, and both honor `Idempotency-Key`, stale requests from evicted workers or double-clicked UI forms cannot jump a workflow ahead twice or resurrect terminal states (`Succeeded`, `Failed`, `Forked`, `Cancelled`).',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A vendor contract review requires a three-day cooling-off timer followed by a legal counter-signature gate. Across that 72-hour span, three API releases restart every application server instance cleanly. Because pause state lives durably in the persisted run row (`WorkflowExecutionState`), no worker thread stalls and the arriving webhook picks up seamlessly at `gateId`.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Reconcile indeterminate runs instead of retrying blindly', href: '/workflows/reconciliation-over-blind-retry' },
            ' — understand how `AwaitingReconciliation` protects external transitions.',
          ],
          [
            { text: 'Sagas and compensating transactions', href: '/workflows/compensating-transactions' },
            ' — recover cleanly when a resumed branch eventually rejects.',
          ],
          [
            { text: 'Shared workflow diagrams (BPMN)', href: '/workflows/workflow-state-machines' },
            ' — align analysts and implementers on gates, timers, and branches before implementation.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'consent-records',
    title: 'Consent records',
    h1: 'Permission that cannot be shown, or withdrawn, was not recorded. It was only assumed.',
    seoTitle: 'Consent records — Workflows | AZOA',
    description:
      'Model user consent as a queryable, distinguishable workflow record with first-class revocation that halts future sharing cleanly.',
    eyebrow: 'CONSENT & ACCESS CONTROL · RECORDS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Many products implement consent as a boolean checked during signup and never looked at again. When a user withdraws permission halfway through a multi-stage workflow — or asks which exact terms they accepted six months ago — an unversioned boolean fails both engineering auditability and data-protection rules.',
    sources: consentSources,
    blocks: [
      {
        type: 'p',
        runs: [
          'An actionable consent model connects permissions directly to execution gates: who granted access, to whom, for which scopes, under which notice, and whether that permission remains unrevoked when a downstream step fires.',
        ],
      },
      { type: 'h2', text: 'Four operational rules from Article 7' },
      {
        type: 'p',
        runs: [
          'Even outside UK or EU jurisdictions, Article 7 of the UK GDPR distills four clean engineering requirements for any software workflow holding user permission:',
        ],
      },
      {
        type: 'ul',
        items: [
          [
            { strong: 'Demonstrable (Article 7(1)):' },
            ' Persist structured consent grant records — grantor, grantee, scoped permissions, source, timestamp, and optional expiry — so evidence exists long after the UI modal closes.',
          ],
          [
            { strong: 'Distinguishable (Article 7(2)):' },
            ' Keep optional data or sharing requests visually and structurally separate from unrelated legal declarations in plain language.',
          ],
          [
            { strong: 'As easy to withdraw as to give (Article 7(3)):' },
            ' Treat revocation as a first-class workflow transition accessible in one deliberate action without burying controls in menus, while keeping prior lawful steps intact.',
          ],
          [
            { strong: 'Unbundled where unnecessary (Article 7(4)):' },
            ' Avoid making unrelated data sharing a hidden precondition for executing core service functionality.',
          ],
        ],
      },
      { type: 'h2', text: 'Wiring consent into AZOA gates' },
      {
        type: 'p',
        runs: [
          'In an AZOA workflow, a consent toggle is checked as an active gate before executing data-sharing or external dispatch steps — never assumed from account creation time. When permission is withdrawn mid-run, downstream steps fail closed while preserving immutable historical audit records of work performed prior to revocation.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A contributor opts into sharing contact details with an external grant reviewer during intake, then withdraws sharing consent two weeks later before payout reporting runs. AZOA records the revocation timestamp immediately, skips downstream contact exports, and leaves earlier approved review logs untouched.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — design zero-friction revocation paths that take effect immediately.',
          ],
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — make every historical permission queryable on demand.',
          ],
          [
            { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
            ' — prevent permission bundling inside general terms.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — ensure grant and withdrawal events enter the audit slice cleanly.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'idempotent-requests',
    title: 'Idempotent retries in a workflow',
    h1: 'Idempotent retries in a workflow',
    seoTitle: 'Idempotent retries in a workflow — Workflows | AZOA',
    description:
      'Apply RFC 9110 HTTP idempotency boundaries cleanly inside workflow run drivers so interrupted transport calls do not create duplicate side effects.',
    eyebrow: 'IDEMPOTENCY & SAGAS · HTTP SEMANTICS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Automatic retry is only safe when executing the identical request twice leaves system state identical to executing it once. Understanding where HTTP semantics guarantee idempotency — and where POST mutations require application-level caller keys — prevents silent duplication across proxies and SDKs.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, June 2022, §9.2.2 (Idempotent Methods).',
        urls: [rfc9110],
        datePublished: '2022-06',
      },
      {
        name: 'Protocol sibling: Idempotent HTTP requests (PrimusNeo).',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
      },
      {
        name: 'Workflow sibling: Settle once.',
        urls: ['/workflows/settle-once'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Transport layers regularly drop packets right after an origin server finishes processing a request. When the HTTP client sees an incomplete read, deciding whether it may automatically resend depends strictly on the contract between caller, proxy, and endpoint semantics.',
        ],
      },
      { type: 'h2', text: 'What RFC 9110 actually permits' },
      {
        type: 'p',
        runs: [
          'RFC 9110 §9.2.2 defines a request method as idempotent if the intended effect on the server of multiple identical requests equals the effect of a single request. In the standard specification, ',
          { strong: 'PUT, DELETE, and safe methods are idempotent' },
          ' — because a client or intermediary can safely repeat them automatically if a communication failure hides the server response.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Crucially, the RFC places explicit ceilings on intermediaries: ',
          { strong: 'a proxy MUST NOT automatically retry non-idempotent requests' },
          ', and ',
          { strong: 'a client SHOULD NOT automatically retry a failed automatic retry' },
          '. Standard ',
          { strong: 'POST' },
          ' calls are not idempotent under HTTP itself; repeating them safely requires explicit application-layer correlation.',
        ],
      },
      { type: 'h2', text: 'Bridging HTTP boundaries inside AZOA' },
      {
        type: 'p',
        runs: [
          'Because workflow mutations (`startWorkflow`, `advance`, `signal`) use `POST` semantics with state transitions, AZOA attaches caller-persisted `Idempotency-Key` headers before submitting and caches request keys across browser reloads until a definite acceptance or refusal lands. See ',
          { text: 'PrimusNeo’s protocol breakdown', href: 'https://primusneo.com/guides/idempotent-requests' },
          ' for dropped-connection wire examples.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A mobile client fires a workflow advance over spotty cellular data, receives an ECONNRESET while the server commits step 2, and a naive proxy tries to resend raw POST without an `Idempotency-Key`. Under RFC 9110 that automatic proxy retry was invalid; pairing explicit idempotency keys with deterministic status reads keeps recovery crisp and deterministic.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — persist initial outcomes and inquire status before resending.',
          ],
          [
            { text: 'Safe methods and idempotent methods', href: '/workflows/safe-and-idempotent' },
            ' — separate read-only safety from repeatable state updates.',
          ],
          [
            { text: 'Reconcile indeterminate runs instead of retrying blindly', href: '/workflows/reconciliation-over-blind-retry' },
            ' — handle transport silence using active state sweeps.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'consent-withdrawal',
    title: 'Withdrawing consent',
    h1: 'Withdrawing consent under UK GDPR Article 7',
    seoTitle: 'Withdrawing consent under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'Implement consent revocation workflows that are as direct as giving permission, halt downstream sharing immediately, and preserve prior audit history.',
    eyebrow: 'CONSENT & ACCESS CONTROL · REVOCATION',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Many applications let a user opt into sharing with a single tap, then bury withdrawal behind support emails or multi-page settings trees. Under Article 7(3), revocation must be as straightforward as consent itself — and in workflow engineering, that withdrawal must propagate immediately to parked or queued steps.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7, UK version, legislation.gov.uk.',
        urls: [articleSeven],
      },
      {
        name: 'Workflow sibling: Consent records.',
        urls: ['/workflows/consent-records'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Consent that cannot be undone cleanly acts like an unbreakable lock. Treating withdrawal as an asynchronous admin ticket guarantees race conditions where scheduled background jobs continue executing operations the user already revoked.',
        ],
      },
      { type: 'h2', text: 'The asymmetric-friction anti-pattern' },
      {
        type: 'p',
        runs: [
          'UK GDPR Article 7(3) establishes four direct operational rules: the person may withdraw consent at any time; prior processing performed before withdrawal remains lawful; they must be informed of the right to withdraw before granting consent; and ',
          { strong: 'it shall be as easy to withdraw as to give consent' },
          '.',
        ],
      },
      {
        type: 'p',
        runs: [
          'In event-driven workflows, that requires two coupled behaviors: exposing a direct one-step withdrawal endpoint next to where grants are managed, and wiring future action-graph gates to re-verify active consent rather than relying on a cached flag from run initiation.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A multi-week cohort workflow accepts a participant’s directory opt-in via a single confirmation toggle, yet forces withdrawal through a three-menu account settings sequence under an unrelated privacy tab. Surfacing revocation directly at the consent grant view and gating upcoming dispatch steps on live consent state closes both usability and compliance gaps cleanly.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — bind consent grants and revocations directly to gate evaluation.',
          ],
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — prove when permission started and when withdrawal took effect.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — block queued downstream steps whenever live permission is absent.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'least-privilege',
    title: 'Least privilege',
    h1: 'Least privilege: only the access the task needs',
    seoTitle: 'Least privilege — Workflows | AZOA',
    description:
      'Apply NIST SP 800-53 AC-6 across background workflow workers and delegated child credentials so each step holds strictly task-scoped permissions.',
    eyebrow: 'CONSENT & ACCESS CONTROL · SCOPED CREDENTIALS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Giving an orchestration worker or service script broad administrative permissions because "it is simpler than writing a custom role" exposes every downstream step to excessive blast radius. Least privilege limits both human operators and background processes to the exact accesses required for their assigned task.',
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
          'In workflow engines where steps trigger external integrations, execute on behalf of tenant users, or sweep reconciliation queues, overly broad credentials quickly become the path of least resistance. A step meant solely to write a receipt ends up carrying cluster-wide settlement authority simply because both shared the same service credential.',
        ],
      },
      { type: 'h2', text: 'Scoping users and automated processes alike' },
      {
        type: 'p',
        runs: [
          'NIST SP 800-53 Rev. 5 (AC-6) specifies that organizations allow only authorized accesses for users — ',
          { strong: 'and processes acting on behalf of users' },
          ' — necessary to accomplish assigned organizational tasks. Its discussion emphasizes intentionally splitting roles, processes, and service accounts throughout development and operations rather than reusing omnipotent super-identities.',
        ],
      },
      {
        type: 'p',
        runs: [
          'In AZOA’s workflow SDK, delegated tenant runs issue short-lived child credentials (`ChildCredentialResult`) whose granted scopes equal strictly the intersection of requested permissions and the tenant’s own delegated authority — passing explicit per-run bearer credentials cleanly rather than issuing permanent admin sessions.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A notification step only needs read permission on run metadata and consent status, yet inherits an operator account holding disbursement and schema-write privileges. Splitting notification workers onto read-scoped credentials prevents template typos or downstream compromises from reaching settlement controls.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Separation of duties', href: '/workflows/separation-of-duties' },
            ' — divide authoring, execution, and approval powers across distinct actors.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — deny execution immediately when a scoped credential lacks required claims.',
          ],
          [
            { text: 'Event logging', href: '/workflows/event-logging' },
            ' — trace exactly which scoped identity executed each step.',
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
      'Use NIST SP 800-53 AC-5 to separate who designs or initiates sensitive workflow runs from who approves or reconciles irreversible transitions.',
    eyebrow: 'CONSENT & ACCESS CONTROL · ROLE BOUNDARIES',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'If the same single account can draft a payout template, publish it live, approve its human gate, and override its reconciliation failure, access control becomes theater. Separating critical workflow duties ensures no lone actor controls both initiation and authorization.',
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
          'Many security incidents inside financial and operational tooling stem not from external attackers breaking cryptography, but from single accounts possessing unsegregated end-to-end capabilities: create vendor, approve invoice, trigger disbursement.',
        ],
      },
      { type: 'h2', text: 'Document the split first, then enforce it in authorizations' },
      {
        type: 'p',
        runs: [
          'NIST SP 800-53 Rev. 5 (AC-5) frames separation of duties in two concrete stages: first identify and document which organization-defined duties require separation, then define system access authorizations to enforce that split. Standard rules don’t prescribe one universal initiator/approver pair for every business — each organization must name its high-risk pairs explicitly and bind roles accordingly.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Inside an AZOA action graph, separation shows up naturally as distinct permission gates: one role authors and publishes the versioned template (`expectedPublishedHash`), an independent actor triggers or consents to a run, and operator-only sweep permissions handle stuck reconciliation probes separately from general members.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'When a workflow allows any engineer with template-edit rights to also self-approve the `AwaitingSignal` release gate on production runs, mistakes and unauthorized edits bypass peer inspection. Enforcing distinct gate-signaler credentials from template-author roles closes the loop cleanly.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Least privilege', href: '/workflows/least-privilege' },
            ' — constrain every individual credential strictly to its narrow role.',
          ],
          [
            { text: 'Pin the published version hash before execution', href: '/workflows/version-pinned-execution' },
            ' — guarantee approvers review the exact template version running.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — halt runs if conflicting actor identities attempt self-approval.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'event-logging',
    title: 'Event logging',
    h1: 'Event logging: decide which events, and who needs them',
    seoTitle: 'Event logging — Workflows | AZOA',
    description:
      'Design purposeful workflow audit slices following NIST SP 800-53 AU-2 by naming exact auditable events and the downstream reviewers who rely on them.',
    eyebrow: 'AUDIT & FAIL-CLOSED GUARDS · AUDIT SLICES',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Teams often say "we log everything" right up until an auditor or incident responder asks why a step stopped or who changed consent — and discovers gigabytes of noisy debug lines with zero structured transition history.',
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
          'An audit log without deliberate selection is just unstructured console noise. Effective logging starts by defining exactly which state transitions constitute the verifiable record and coordinating those schemas with the teams who review compliance, reconciliation, and security alerts.',
        ],
      },
      { type: 'h2', text: 'Define auditable event types and stakeholders upfront' },
      {
        type: 'p',
        runs: [
          'NIST SP 800-53 Rev. 5 (AU-2) begins with two foundational requirements: identify the specific event types the system must log in support of the audit function, and coordinate logging scope across organizational entities that depend on those audit records.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Across AZOA runs, meaningful audit slices focus on decision boundaries rather than dumping secrets or transient stack chatter: consent grants and revocations, published hash pins (`publishedVersionHash`), fail-closed gate refusals, forks (`parentRunId`, `forkReason`), and reconciliation sweep outcomes.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Three months after an interrupted multi-step disbursement, finance needs proof of whether an operator manually resumed `Suspended` step 3 or if the timer fired automatically. Recording typed transition events (`NodeExecution`, `forkReason`, `failReason`, actor subject, timestamp) turns incident post-mortems from archaeology into a one-query audit slice.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — capture consent grants and withdrawals as durable event rows.',
          ],
          [
            { text: 'Durable waits, external signals, and stale threads', href: '/workflows/durable-waits-and-signals' },
            ' — preserve full step-by-step execution projections across multi-day pauses.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — record explicit refusal reasons cleanly whenever a guard blocks progress.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'workflow-state-machines',
    title: 'What is BPMN',
    h1: 'Shared process diagrams: BPMN and workflow state machines',
    seoTitle: 'Shared process diagrams (BPMN) — Workflows | AZOA',
    description:
      'Use standardized process diagrams like OMG BPMN 2.0.2 so product owners, operators, and software engineers share one exact picture before encoding state machines.',
    eyebrow: 'AUDIT & FAIL-CLOSED GUARDS · PROCESS MODELING',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'When a multi-step approval workflow lives only inside scattered Slack threads and controller conditionals, product analysts, reviewers, and backend engineers carry three incompatible mental models of what happens on failure.',
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
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Before implementing gates, compensating paths, or escalation timers in code, teams need an unambiguous visual diagram showing happy paths, parallel splits, waits, and refusal exits that non-engineers and implementers read the same way.',
        ],
      },
      { type: 'h2', text: 'Bridging business intent and executable steps' },
      {
        type: 'p',
        runs: [
          'OMG’s ',
          { strong: 'BPMN Version 2.0.2' },
          ' standard was created specifically to bridge the gap between process design and software implementation — giving business analysts, technical developers who build the execution steps, and managers monitoring live runs one shared visual vocabulary independent of a particular runtime.',
        ],
      },
      {
        type: 'p',
        runs: [
          'While AZOA compiles workflows into its own typed directed-acyclic action graph (`WorkflowExecutionState`) rather than executing raw BPMN XML directly, drawing flows using clear state-machine and BPMN conventions upfront surfaces missing timeout paths and escalation gates long before deployment.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Three engineers disagree on whether a rejected milestone inspection cancels the entire grant run or routes back for amendment. Sketching the explicit state transitions, timers, and compensation exits first turns implicit assumptions into explicit graph edges before anyone writes controller handlers.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Sagas and compensating transactions', href: '/workflows/compensating-transactions' },
            ' — model backward unwinding paths cleanly on your process map.',
          ],
          [
            { text: 'Durable waits, external signals, and stale threads', href: '/workflows/durable-waits-and-signals' },
            ' — translate visual gates and timers cleanly into AZOA run states.',
          ],
          [
            { text: 'Pin the published version hash before execution', href: '/workflows/version-pinned-execution' },
            ' — guarantee deployed workflows match the reviewed version.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'compensating-transactions',
    title: 'What is a saga',
    h1: 'Sagas and compensating transactions in long-lived workflows',
    seoTitle: 'What is a saga in a long-lived transaction — Workflows | AZOA',
    description:
      'Coordinate multi-step workflows that span external boundaries without global locks by pairing each committed step with an explicit compensating transaction.',
    eyebrow: 'IDEMPOTENCY & SAGAS · COMPENSATING ACTIONS',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Long-running business processes cannot hold open database locks while waiting hours or days for external services and approvals. A saga breaks a long-lived transaction into a sequence of discrete steps — guaranteeing that either every step succeeds, or compensating actions cleanly amend a partial run.',
    sources: [
      {
        name: 'Hector Garcia-Molina and Kenneth Salem, “Sagas,” Department of Computer Science, Princeton University.',
        urls: ['https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Two-phase commit breaks down the moment a workflow spans independent HTTP services, third-party clearing partners, or human decision delays. Holding locks across external steps stalls unrelated throughput and becomes fragile across process crashes.',
        ],
      },
      { type: 'h2', text: 'Forward completion or backward semantic repair' },
      {
        type: 'p',
        runs: [
          'In Garcia-Molina and Salem’s foundational ',
          { em: 'Sagas' },
          ' model, a long-lived transaction (LLT) is structured as interleaved smaller transactions where the system guarantees an all-or-compensated invariant: either all transactions in the sequence complete successfully, or explicit ',
          { strong: 'compensating transactions' },
          ' execute to semantically amend the effects of a partial failure.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Because a committed external action cannot simply vanish via database rollback, compensation writes an explicit corrective action forward in the log — releasing an escrow hold, issuing a refund adjustment, or revoking a provisioned permission — preserving a full audit trail of both the initial step and its reversal.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Step 1 locks reserve escrow, Step 2 registers milestone acceptance, and Step 3 fails permanently at the partner API. Rather than leaving Step 1 orphaned in limbo or manually editing database tables afterward, an explicit compensating step releases the held reserve and closes the run in `Failed` with full linkage between original hold and release.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Reconcile indeterminate runs instead of retrying blindly', href: '/workflows/reconciliation-over-blind-retry' },
            ' — verify an external step truly failed before triggering compensation.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — ensure compensating steps carry their own idempotency keys.',
          ],
          [
            { text: 'Durable waits, external signals, and stale threads', href: '/workflows/durable-waits-and-signals' },
            ' — park sagas safely across timers and signals without lock contention.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'safe-and-idempotent',
    title: 'Safe methods and idempotent methods',
    h1: 'Safe methods and idempotent methods',
    seoTitle: 'Safe methods and idempotent methods — Workflows | AZOA',
    description:
      'Keep RFC 9110 distinctions crystal clear: safe methods like GET are read-only and idempotent, while PUT and DELETE are idempotent without being safe.',
    eyebrow: 'IDEMPOTENCY & SAGAS · SAFE VS IDEMPOTENT',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      '"Safe to read" and "safe to retry" solve two entirely different problems. Treating every retryable workflow endpoint as harmless—or worse, hiding destructive mutations behind HTTP GET URLs—breaks caches, crawlers, and automatic retry safety.',
    sources: [
      {
        name: 'RFC 9110, HTTP Semantics, §9.2.1 (Safe Methods) and §9.2.2 (Idempotent Methods).',
        urls: [rfc9110],
        datePublished: '2022-06',
      },
      {
        name: 'Workflow sibling: Idempotent retries in a workflow.',
        urls: ['/workflows/idempotent-requests'],
      },
      {
        name: 'Protocol sibling: Idempotent HTTP requests (PrimusNeo).',
        urls: ['https://primusneo.com/guides/idempotent-requests'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Every safe HTTP method in RFC 9110 is idempotent, but plenty of idempotent methods mutate critical state. Confusing the two leads engineers to expose destructive triggers as GET queries or strip confirmation guards from repeatable mutations.',
        ],
      },
      { type: 'h2', text: 'Read-only semantics vs. repeatable mutations' },
      {
        type: 'p',
        runs: [
          'Under ',
          { strong: 'RFC 9110 §9.2.1' },
          ', `GET`, `HEAD`, `OPTIONS`, and `TRACE` are defined as ',
          { strong: 'safe' },
          ': their semantics are essentially read-only, allowing prefetchers, monitoring bots, and caches to call them freely without expecting state changes. The specification warns explicitly that resource owners MUST disable unsafe actions if accessed via a safe method — famously illustrated with a URI such as `page?do=delete` reached over GET.',
        ],
      },
      {
        type: 'p',
        runs: [
          'Under ',
          { strong: 'RFC 9110 §9.2.2' },
          ', `PUT` and `DELETE` are ',
          { strong: 'idempotent without being safe' },
          ': they clearly change server state, yet sending the exact request twice produces the same intended outcome as sending it once. Neither safe nor idempotent includes bare `POST`, which must carry explicit keys or status reconciliation before any retry.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'Suppose a preview email includes a quick-action approval link backed by GET (`?action=release`). A corporate mail security scanner prefetches every URL inside incoming messages—triggering approval before any human reads the email. Keeping read endpoints strictly side-effect-free while executing state advances over explicit authenticated POST calls with idempotency keys prevents automated prefetchers from acting as inadvertent approvers.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Idempotent retries in a workflow', href: '/workflows/idempotent-requests' },
            ' — wire RFC 9110 retry bounds cleanly across SDK transport.',
          ],
          [
            { text: 'Settle once', href: '/workflows/settle-once' },
            ' — bind workflow mutations to explicit caller idempotency keys.',
          ],
          [
            { text: 'Fail-closed controls', href: '/workflows/fail-closed' },
            ' — refuse state transitions triggered over read-only paths.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'demonstrable-consent',
    title: 'Consent must be demonstrable',
    h1: 'Consent must be demonstrable under UK GDPR Article 7',
    seoTitle: 'Consent must be demonstrable under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'Store immutable evidence of who granted permission, for which scopes, and under which disclosure hash so consent can always be proved after the fact.',
    eyebrow: 'CONSENT & ACCESS CONTROL · AUDITABILITY',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'A single `accepted_terms = true` column updated in place destroys history the moment policies change or users withdraw later. Demonstrable consent means preserving verifiable evidence of what was accepted, by whom, and when.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7(1) (UK GDPR on legislation.gov.uk).',
        urls: [articleSeven],
      },
      {
        name: 'Workflow siblings: Consent records & Version-pinned execution.',
        urls: ['/workflows/consent-records', '/workflows/version-pinned-execution'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'If processing or third-party data disclosure relies on participant consent, Article 7(1) makes the operational test unambiguous: ',
          { strong: 'where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented' },
          '.',
        ],
      },
      { type: 'h2', text: 'Why overwrite-in-place database flags break proof' },
      {
        type: 'p',
        runs: [
          'Updating a mutable boolean on a user profile erases prior scope versions and breaks causality once a workflow takes downstream action. An audit-ready schema keeps consent grants as append-friendly records referencing actor ID, receiver ID, explicit permission list, acquisition channel, grant timestamp, optional expiry, and linked withdrawal timestamps — paired with `expectedPublishedHash` when triggering economic disclosures.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'When a participant disputes why their email appeared on a reviewer export three weeks prior, querying the run’s audit slice returns the exact scoped consent record created before dispatch — proving both valid authorization at run time and whether a later withdrawal halted subsequent runs.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — tie stored grants directly into active workflow gate checks.',
          ],
          [
            { text: 'Consent must be clearly distinguishable', href: '/workflows/distinguishable-consent' },
            ' — keep optional consents visibly separate before recording them.',
          ],
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — record revocations cleanly without mutating historical facts.',
          ],
        ],
      },
    ],
  },
  {
    slug: 'distinguishable-consent',
    title: 'Consent must be clearly distinguishable',
    h1: 'Consent must be clearly distinguishable under UK GDPR Article 7',
    seoTitle: 'Consent must be clearly distinguishable under UK GDPR Article 7 — Workflows | AZOA',
    description:
      'Keep optional workflow permissions visually and structurally separate from unrelated legal declarations in plain language.',
    eyebrow: 'CONSENT & ACCESS CONTROL · CLARITY',
    publishedAt: PUBLISHED,
    lastReviewed: UPDATED,
    lede:
      'Burying optional data-sharing agreements inside a 4,000-word Terms of Service checkbox invalidates the permission you thought you captured. Article 7(2) states that any bundled, indistinguishable consent request infringing those rules is simply not binding.',
    sources: [
      {
        name: 'Regulation (EU) 2016/679, Article 7(2) and 7(4) (UK GDPR on legislation.gov.uk).',
        urls: [articleSeven],
      },
      {
        name: 'Workflow sibling: Consent must be demonstrable.',
        urls: ['/workflows/demonstrable-consent'],
      },
    ],
    blocks: [
      {
        type: 'p',
        runs: [
          'Under ',
          { strong: 'UK GDPR Article 7(2)' },
          ', when consent sits inside a written declaration covering other matters, the request must be presented in a manner ',
          { strong: 'clearly distinguishable from the other matters' },
          ', intelligible, easily accessible, and written in clear plain language — otherwise ',
          { strong: 'any infringing part shall not be binding' },
          '. Alongside Article 7(4), workflows must avoid making core service delivery conditional on consent unnecessary for contract performance.',
        ],
      },
      { type: 'h2', text: 'Designing clean workflow consent toggles' },
      {
        type: 'p',
        runs: [
          'Instead of one blanket "I agree to everything" checkbox gating form submission, decompose workflow intake into explicit, separate permission toggles tied to discrete scopes. Users can approve core execution while leaving optional sharing unchecked — and the workflow engine checks only the exact scope required for each step.',
        ],
      },
      { type: 'h2', text: 'In practice' },
      {
        type: 'p',
        runs: [
          'A project onboarding screen bundles marketing newsletters, public profile indexing, and payout agreement inside one monolithic checkbox. Separating optional directory visibility and notifications into independent plain-language consent toggles preserves genuine user agency and keeps every permission scope unambiguous in the audit log.',
        ],
      },
      { type: 'sources' },
      { type: 'h2', text: 'Also in this section' },
      {
        type: 'ul',
        items: [
          [
            { text: 'Consent must be demonstrable', href: '/workflows/demonstrable-consent' },
            ' — record separate proof per distinct consent scope.',
          ],
          [
            { text: 'Consent records', href: '/workflows/consent-records' },
            ' — wire granular scope permissions cleanly into run gates.',
          ],
          [
            { text: 'Withdrawing consent', href: '/workflows/consent-withdrawal' },
            ' — let participants toggle individual scopes off just as easily.',
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
