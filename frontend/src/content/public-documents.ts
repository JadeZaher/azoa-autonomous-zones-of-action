import { SISTER_SITES } from '@/components/landing/sisters'

export const SITE = 'https://azoa.ardanova.com'
export const SHARE_IMAGE = `${SITE}/og/azoa-og-1200x630.png`
export const AUTHOR = 'Escherbridge'
export const OPERATOR_NAME = 'Escherbridge, LLC'
export const OPERATOR_EMAIL = 'contact@escherbridge.com'

export const SIMULATION =
  'This hosted site is a simulation. Real-value bridging is disabled. It does not move value.'

const PUBLIC_PREFIXES = ['/architecture', '/federation', '/privacy', '/terms', '/notes', '/blog', '/news', '/workflows']

/** Private app routes. Public HTML must not point at these, and robots.txt disallows them. */
export const PRIVATE_PREFIXES = [
  '/login',
  '/register',
  '/callback',
  '/linked-accounts',
  '/operator',
  '/api',
  '/overview',
  '/actions',
  '/action-graph-bundle',
  '/admin',
  '/analytics',
  '/api-keys',
  '/blockchain',
  '/bridge',
  '/business-verification',
  '/cash-out',
  '/funds',
  '/fungible-mint',
  '/holds',
  '/holon-query',
  '/holons',
  '/kyc',
  '/nfts',
  '/node-transparency',
  '/payout-balance',
  '/search',
  '/settings',
  '/swap',
  '/tests',
  '/user-nfts',
  '/users',
  '/verify-identity',
  '/wallets',
] as const

export type PublicKind = 'website' | 'article'

export type PublicDocument = {
  path: string
  title: string
  description: string
  kind: PublicKind
  index: boolean
  inSitemap: boolean
  publishedAt?: string
}

export function requestPath(headerValue: string | null): string | null {
  if (!headerValue || !headerValue.startsWith('/') || headerValue.startsWith('//')) return null
  const path = headerValue.split('?')[0]?.replace(/\/+$/, '') || '/'
  return path === '' ? '/' : path
}

export function isPublicDocument(pathname: string): boolean {
  if (pathname === '/') return true
  return PUBLIC_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))
}

/** The browser API origin is omitted from public HTML so a Railway host cannot leak there. */
export function injectsRuntimeApi(pathname: string | null): boolean {
  return pathname === null || !isPublicDocument(pathname)
}

export function absoluteUrl(path: string): string {
  return path === '/' ? SITE : `${SITE}${path}`
}

export function formatPublicDate(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number)
  const months = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
  return `${day} ${months[(month ?? 1) - 1]} ${year}`
}

const DEFAULT_DESCRIPTION =
  'Open-source engine for structured workflows. This hosted site is a simulation: real-value bridging is disabled, and fiat settlement is not enabled yet.'

export const ESSAYS = [
  {
    slug: 'settle-once-or-not-at-all',
    title: 'A retried step should settle once, or not at all',
    description:
      'A timeout is not a decision. AZOA is being built so a retried step can be found later as a yes or a no. This public site is a simulation, and it does not move value.',
    publishedAt: '2026-11-09',
    summary:
      'A timeout is not a decision. AZOA is being built so a retried step can be found later as a yes or a no. This public site is a simulation, and it does not move value.',
  },
  {
    slug: 'fail-closed-or-it-isnt-a-control',
    title: "Fail closed, or it isn't a control",
    description:
      'A control that fails open is a decoration. AZOA is built so that anything it cannot verify is refused, not waved through. This public site is a simulation, and that sentence is part of the control, not a disclaimer under it.',
    publishedAt: '2026-11-09',
    summary:
      'A control that fails open is a decoration. AZOA is built so that anything it cannot verify is refused. This public site is a simulation, and that sentence is part of the control.',
  },
] as const

export type Essay = (typeof ESSAYS)[number]

export function essayPath(slug: string): string {
  return `/notes/${slug}`
}

export function essayBySlug(slug: string): Essay | undefined {
  return ESSAYS.find((essay) => essay.slug === slug)
}

export const WORKFLOWS = [
  {
    slug: 'settle-once',
    title: 'Settle once',
    seoTitle: 'Settle once — Workflows — AZOA',
    description:
      'A retried step is modelled to settle once, or not at all. This hosted site is a simulation. Real-value bridging is disabled, and it does not move value.',
    publishedAt: '2026-11-10',
    summary: 'The model looks up an earlier attempt before it treats a retry as new work. This site does not move value.',
    paragraphs: [
      'A timeout is a silence. If the same work is sent again because nobody can tell whether the first attempt landed, the second attempt can become a second result. AZOA is being built so a step carries an identity that a later attempt can find. The later attempt is the same work, not a new one.',
      'On the public homepage that rule is stated as a model: a retried or duplicated step is modelled to settle once, or not at all. This site does not move value. The longer note is the essay from 9 November 2026.',
      'The architecture page describes the same shape. A graph can park, resume, and check the world outside the graph before an irreversible step. Those checks run here against a simulation. They are not a promise that a payment has been made, because this hosted site does not make one.',
      SIMULATION,
    ],
  },
  {
    slug: 'fail-closed',
    title: 'Fail-closed controls',
    seoTitle: 'Fail-closed controls — Workflows — AZOA',
    description:
      'A check that cannot be verified is refused. This hosted site is a simulation. Real-value bridging is disabled, and it does not move value.',
    publishedAt: '2026-11-10',
    summary: 'If a step cannot be verified, the model refuses it. This site does not move value.',
    paragraphs: [
      'A control that lets work continue when the check cannot be finished has already given up. The convenient path is a warning in a log. The model AZOA is built around does the other thing: if the check cannot be verified, the step is refused.',
      'That sentence is already on the homepage, and the 9 November 2026 note argues it as a construction rule rather than a slogan. This page is the same rule named as a workflow shape. It is not a claim that a dangerous path is open on this site.',
      'What you can inspect today is the published description: the architecture, the terms, and the two notes. The terms say not to send funds here and not to expect a payment. The refusal is part of the page, in the same way a refused step is part of a graph.',
      SIMULATION,
    ],
  },
  {
    slug: 'consent-records',
    title: 'Consent records',
    seoTitle: 'Consent records — Workflows — AZOA',
    description:
      'A permission record says who may act, for what, and that it can be revoked. This hosted site is a simulation and does not move value.',
    publishedAt: '2026-11-10',
    summary: 'The model keeps a permission that can be read and revoked. This site does not move value.',
    paragraphs: [
      'A workflow that acts for someone needs a record of that permission: who granted it, what it covers, and how it is taken back. The homepage states the primitive in one line. Apps act for you only with consent you can see, and revoke, at any moment.',
      'This page is that shape, named as a workflow. It is a record in the model. It is not an identity-provider product, and it is not a statement that this hosted site is checking documents or opening an account at a partner.',
      'Withdrawing the permission ends the grant. The architecture page says the same thing without dressing it as a finished service: the app loses the permission to act for you. On this site that permission does not move money.',
      SIMULATION,
    ],
  },
  {
    slug: 'action-graphs',
    title: 'Action graphs',
    seoTitle: 'Action graphs — Workflows — AZOA',
    description:
      'An action is a graph of steps that can wait and resume. This hosted site shows that shape as a simulation and does not move value.',
    publishedAt: '2026-11-10',
    summary: 'Steps, waits, and checks are one graph. The public demo graph is a demo. This site does not move value.',
    paragraphs: [
      'An action, in this engine, is a graph of steps. A step can wait. A later signal can resume it. A failed check can stop the steps after it. The architecture page draws that as a durable state machine, and the homepage shows a demo graph labelled as a demo.',
      'The demo is not a settlement. The badge on the homepage says the graph is simulated. Publishing a picture of a graph, or letting someone walk through one, does not move value and does not open a payment path.',
      'Bundles, on the same page, are how a product can be scaffolded from those steps. Scaffolding is not a launch. The source is Apache-2.0 and can be self-hosted. The hosted site remains the simulation described in the terms.',
      SIMULATION,
    ],
  },
  {
    slug: 'grants-and-bounties',
    title: 'Grants and bounties',
    seoTitle: 'Grants and bounties — Workflows — AZOA',
    description:
      'A grant or bounty is a modelled release that waits on a check. This hosted site does not pay anyone. Real-value bridging is disabled.',
    publishedAt: '2026-11-10',
    summary: 'Applications, milestones, and a release that waits on a check. This site does not pay a grant or a bounty.',
    paragraphs: [
      'The homepage lists grants and bounties as one of four modelled economies. The sentence under that heading is the whole claim: applications and milestones, with a release that waits on a check. This site does not release funds or send a refund.',
      'A payroll line, a supplier milestone, and a bounty can share a shape in the model. Sharing a shape is not a payment. The large note on the homepage says this site does not pay a supplier, run payroll, or pay yield.',
      'ArdaNova is the planned pilot named on the federation page. It is a separate product, its economy is still simulated, and this site does not pay creators for it. A modelled grant is not an announcement that a grant program is open.',
      SIMULATION,
    ],
  },
] as const

export type Workflow = (typeof WORKFLOWS)[number]

export function workflowBySlug(slug: string): Workflow | undefined {
  return WORKFLOWS.find((workflow) => workflow.slug === slug)
}

const MARKETING: PublicDocument[] = [
  {
    path: '/',
    title: 'AZOA — Autonomous Zones of Action',
    description: DEFAULT_DESCRIPTION,
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/architecture',
    title: 'Architecture — AZOA',
    description:
      'A workflow the engine can check. This hosted site runs that substrate as a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/federation',
    title: 'Federation — AZOA',
    description:
      'A plan for zones that keep their own records. Federation is not running on this site. The hosted site is a simulation, and real-value bridging is disabled.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/privacy',
    title: 'Privacy — AZOA',
    description:
      'What the hosted AZOA site stores: account fields, browser storage, optional provider sign-in, and verification records if a session is started.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/terms',
    title: 'Terms — AZOA',
    description:
      'Pre-launch terms for the hosted AZOA site. The site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/notes',
    title: 'Notes — AZOA',
    description:
      'Two notes on settlement and controls for this hosted simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/blog',
    title: 'Blog — AZOA',
    description:
      'Two notes by Escherbridge on settlement and controls. This hosted site is a simulation. Real-value bridging is disabled, and it does not move value.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/news',
    title: 'News — AZOA',
    description:
      'No public announcement is listed yet. This hosted site is a simulation. Real-value bridging is disabled, and it does not move value.',
    kind: 'website',
    index: false,
    inSitemap: false,
  },
  {
    path: '/workflows',
    title: 'Workflows — AZOA',
    description:
      'Five workflow shapes the engine models. This hosted site is a simulation. Real-value bridging is disabled, and it does not move value.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
]

export function publicDocuments(): PublicDocument[] {
  const essays: PublicDocument[] = ESSAYS.map((essay) => ({
    path: essayPath(essay.slug),
    title: `${essay.title} — AZOA`,
    description: essay.description,
    kind: 'article',
    index: true,
    inSitemap: true,
    publishedAt: essay.publishedAt,
  }))
  const workflows: PublicDocument[] = WORKFLOWS.map((workflow) => ({
    path: `/workflows/${workflow.slug}`,
    title: workflow.seoTitle,
    description: workflow.description,
    kind: 'article',
    index: true,
    inSitemap: true,
    publishedAt: workflow.publishedAt,
  }))
  return [...MARKETING, ...essays, ...workflows]
}

export function publicDocument(pathname: string): PublicDocument | undefined {
  return publicDocuments().find((document) => document.path === pathname)
}

export function sitemapDocuments(): PublicDocument[] {
  return publicDocuments().filter((document) => document.inSitemap)
}

export function organizationJsonLd() {
  return {
    '@type': 'Organization',
    '@id': `${SITE}/#organization`,
    name: 'AZOA',
    url: SITE,
    logo: `${SITE}/brand/azoa-logo.png`,
    email: OPERATOR_EMAIL,
    parentOrganization: {
      '@type': 'Organization',
      name: OPERATOR_NAME,
      email: OPERATOR_EMAIL,
    },
    sameAs: SISTER_SITES.map((site) => site.href),
  }
}

export function websiteJsonLd() {
  return {
    '@type': 'WebSite',
    '@id': `${SITE}/#website`,
    name: 'AZOA',
    url: SITE,
    inLanguage: 'en-US',
    publisher: { '@id': `${SITE}/#organization` },
  }
}

export function breadcrumbJsonLd(path: string, title: string) {
  const crumbs = [{ name: 'Home', path: '/' }]
  if (path.startsWith('/workflows/')) crumbs.push({ name: 'Workflows', path: '/workflows' })
  if (path.startsWith('/notes/')) crumbs.push({ name: 'Notes', path: '/notes' })
  if (path.startsWith('/blog')) crumbs.push({ name: 'Blog', path: '/blog' })
  if (path !== '/' && !crumbs.some((crumb) => crumb.path === path)) crumbs.push({ name: title, path })
  return {
    '@type': 'BreadcrumbList',
    itemListElement: crumbs.map((crumb, index) => ({
      '@type': 'ListItem',
      position: index + 1,
      name: crumb.name,
      item: absoluteUrl(crumb.path),
    })),
  }
}

export function pageJsonLd(pathname: string): Record<string, unknown>[] {
  const document = publicDocument(pathname)
  if (!document) return []
  const graph: Record<string, unknown>[] = [breadcrumbJsonLd(pathname, document.title)]
  if (pathname === '/blog' || pathname === '/notes') {
    graph.push({
      '@type': 'CollectionPage',
      name: document.title,
      url: absoluteUrl(pathname),
      isPartOf: { '@id': `${SITE}/#website` },
      mainEntity: {
        '@type': 'ItemList',
        itemListElement: ESSAYS.map((essay, index) => ({
          '@type': 'ListItem',
          position: index + 1,
          url: absoluteUrl(essayPath(essay.slug)),
          name: essay.title,
        })),
      },
    })
  } else if (pathname === '/workflows') {
    graph.push({
      '@type': 'CollectionPage',
      name: document.title,
      url: absoluteUrl(pathname),
      isPartOf: { '@id': `${SITE}/#website` },
      mainEntity: {
        '@type': 'ItemList',
        itemListElement: WORKFLOWS.map((workflow, index) => ({
          '@type': 'ListItem',
          position: index + 1,
          url: absoluteUrl(`/workflows/${workflow.slug}`),
          name: workflow.title,
        })),
      },
    })
  } else if (document.kind === 'article' && document.publishedAt) {
    const workflow = WORKFLOWS.find((item) => `/workflows/${item.slug}` === pathname)
    graph.push({
      '@type': 'Article',
      headline: document.title,
      description: document.description,
      datePublished: document.publishedAt,
      author: { '@type': 'Organization', name: AUTHOR },
      publisher: { '@id': `${SITE}/#organization` },
      mainEntityOfPage: absoluteUrl(pathname),
      image: SHARE_IMAGE,
      citation: workflow
        ? [
            { '@type': 'CreativeWork', name: 'AZOA homepage', url: SITE },
            { '@type': 'CreativeWork', name: 'AZOA architecture', url: `${SITE}/architecture` },
            { '@type': 'CreativeWork', name: 'AZOA terms', url: `${SITE}/terms` },
          ]
        : undefined,
    })
  } else {
    graph.push({
      '@type': 'WebPage',
      name: document.title,
      description: document.description,
      url: absoluteUrl(pathname),
      isPartOf: { '@id': `${SITE}/#website` },
    })
  }
  return graph
}

export function jsonLdGraph(pathname: string | null) {
  const pageNodes = pathname && isPublicDocument(pathname) ? pageJsonLd(pathname) : []
  return {
    '@context': 'https://schema.org',
    '@graph': [organizationJsonLd(), websiteJsonLd(), ...pageNodes],
  }
}

function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

export function feedItems() {
  const essays = ESSAYS.map((essay) => ({
    path: essayPath(essay.slug),
    title: essay.title,
    description: essay.description,
    publishedAt: essay.publishedAt,
  }))
  const workflows = WORKFLOWS.map((workflow) => ({
    path: `/workflows/${workflow.slug}`,
    title: workflow.title,
    description: workflow.description,
    publishedAt: workflow.publishedAt,
  }))
  return [...workflows, ...essays].sort((a, b) => (a.publishedAt < b.publishedAt ? 1 : -1))
}

export function buildFeedXml(): string {
  const items = feedItems()
    .map((item) => {
      const link = absoluteUrl(item.path)
      return [
        '<item>',
        `<title>${escapeXml(item.title)}</title>`,
        `<link>${link}</link>`,
        `<guid>${link}</guid>`,
        `<pubDate>${item.publishedAt}</pubDate>`,
        `<description>${escapeXml(item.description)}</description>`,
        `<author>${OPERATOR_EMAIL} (${AUTHOR})</author>`,
        '</item>',
      ].join('')
    })
    .join('')
  return `<?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>AZOA</title><link>${SITE}</link><description>${escapeXml(SIMULATION)}</description>${items}</channel></rss>`
}
