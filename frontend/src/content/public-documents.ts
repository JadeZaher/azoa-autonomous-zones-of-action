import { SISTER_SITES } from '@/components/landing/sisters'
import { WORKFLOW_INDEX, WORKFLOWS, workflowBySlug, workflowCitations } from '@/content/workflows'

export { WORKFLOWS, workflowBySlug }
export type { WorkflowPage as Workflow } from '@/content/workflows'

export const SITE = 'https://azoa.ardanova.com'
export const SHARE_IMAGE = `${SITE}/og/azoa-og-1200x630.png`
export const SHARE_IMAGE_META = {
  url: SHARE_IMAGE,
  width: 1200,
  height: 630,
  alt: 'AZOA — Autonomous Zones of Action',
} as const
export const AUTHOR = 'Escherbridge'
export const OPERATOR_NAME = 'Escherbridge, LLC'
export const OPERATOR_EMAIL = 'contact@escherbridge.com'

export const SIMULATION =
  'Hosted demo runs simulated payloads. Real-value bridging and fiat settlement are disabled; self-host Apache-2.0 source for production workloads.'

const PUBLIC_PREFIXES = ['/architecture', '/federation', '/privacy', '/terms', '/contact', '/notes', '/blog', '/news', '/workflows']

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
  modifiedAt?: string
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
  if (pathname === null) return true
  if (isPublicDocument(pathname)) return false
  return PRIVATE_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))
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
  'Open-source workflow engine for durable action graphs: explicit idempotency keys, reconciliation sweeps, consent records, and fail-closed controls.'

export const ESSAYS = [
  {
    slug: 'settle-once-or-not-at-all',
    title: 'A retried step should settle once, or not at all',
    description:
      'A timeout is network silence, not a verdict. How AZOA binds retries to durable idempotency keys and active reconciliation sweeps.',
    publishedAt: '2026-11-09',
    summary:
      'Why optimistic retry loops fail on network timeouts, and how explicit idempotency keys plus AwaitingReconciliation resolve ambiguous responses.',
  },
  {
    slug: 'fail-closed-or-it-isnt-a-control',
    title: "Fail closed, or it isn't a control",
    description:
      'A safeguard that logs a warning and continues is a decoration. How AZOA enforces fail-closed version pinning, authority checks, and consent gates.',
    publishedAt: '2026-11-09',
    summary:
      'Why unreadable permissions, stale version hashes, and revoked consent must halt execution outright instead of writing quiet log warnings.',
  },
] as const

export type Essay = (typeof ESSAYS)[number]

export function essayPath(slug: string): string {
  return `/notes/${slug}`
}

export function essayBySlug(slug: string): Essay | undefined {
  return ESSAYS.find((essay) => essay.slug === slug)
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
      'How AZOA structures durable action graphs, version-pinned pre-flight consent, explicit idempotency keys, and swappable settlement adapters.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/federation',
    title: 'Federation — AZOA',
    description:
      'Architecture sketch for independent workspace zones that exchange consent-scoped verifiable records without central lock-in.',
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
      'Pre-launch terms for the hosted AZOA site. Hosted demo runs simulated payloads; real-value bridging and fiat settlement are disabled.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/contact',
    title: 'Contact — AZOA',
    description:
      'Contact Escherbridge, LLC in Boise, Idaho at contact@escherbridge.com. The hosted AZOA demo runs simulated payloads. Real-value bridging is disabled.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/notes',
    title: 'Notes — AZOA',
    description:
      'Engineering notes by Escherbridge on idempotency keys, reconciliation sweeps, and fail-closed design. Hosted demo runs simulated payloads.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/blog',
    title: 'Blog — AZOA',
    description:
      'Engineering blog and workflow pattern deep dives by Escherbridge on durable execution, consent guards, and fail-closed controls.',
    kind: 'website',
    index: true,
    inSitemap: true,
  },
  {
    path: '/news',
    title: 'News — AZOA',
    description:
      'Release milestones and engineering updates for the AZOA open-source workflow engine.',
    kind: 'website',
    index: false,
    inSitemap: false,
  },
  {
    path: '/workflows',
    title: WORKFLOW_INDEX.seoTitle,
    description: WORKFLOW_INDEX.description,
    kind: 'website',
    index: true,
    inSitemap: true,
    publishedAt: WORKFLOW_INDEX.publishedAt,
    modifiedAt: WORKFLOW_INDEX.lastReviewed,
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
    modifiedAt: workflow.lastReviewed,
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
      address: {
        '@type': 'PostalAddress',
        addressLocality: 'Boise',
        addressRegion: 'ID',
        addressCountry: 'US',
      },
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

function breadcrumbLabel(title: string): string {
  return title.replace(/ (?:—|\|) AZOA$/, '')
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
      name: breadcrumbLabel(crumb.name),
      item: absoluteUrl(crumb.path),
    })),
  }
}

export function pageJsonLd(pathname: string): Record<string, unknown>[] {
  const document = publicDocument(pathname)
  if (!document) return []
  const workflow = WORKFLOWS.find((item) => `/workflows/${item.slug}` === pathname)
  const crumbTitle = pathname === '/workflows' ? 'Workflows' : workflow?.title ?? document.title
  const graph: Record<string, unknown>[] = [breadcrumbJsonLd(pathname, crumbTitle)]
  if (pathname === '/') {
    const sections = publicDocuments().filter(
      (item) => item.index && item.path !== '/' && !item.path.slice(1).includes('/'),
    )
    graph.push({
      '@type': 'CollectionPage',
      name: document.title,
      description: document.description,
      url: absoluteUrl('/'),
      isPartOf: { '@id': `${SITE}/#website` },
      mainEntity: {
        '@type': 'ItemList',
        itemListElement: sections.map((item, index) => ({
          '@type': 'ListItem',
          position: index + 1,
          url: absoluteUrl(item.path),
          name: breadcrumbLabel(item.title),
        })),
      },
    })
  } else if (pathname === '/blog' || pathname === '/notes') {
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
      name: WORKFLOW_INDEX.h1,
      url: absoluteUrl(pathname),
      isPartOf: { '@id': `${SITE}/#website` },
      datePublished: WORKFLOW_INDEX.publishedAt,
      dateModified: WORKFLOW_INDEX.lastReviewed,
      author: { '@type': 'Organization', name: AUTHOR },
      publisher: {
        '@type': 'Organization',
        name: AUTHOR,
        parentOrganization: { '@type': 'Organization', name: OPERATOR_NAME },
      },
      mainEntity: {
        '@type': 'ItemList',
        itemListElement: WORKFLOWS.map((item, index) => ({
          '@type': 'ListItem',
          position: index + 1,
          url: absoluteUrl(`/workflows/${item.slug}`),
          name: item.title,
        })),
      },
    })
  } else if (workflow) {
    graph.push({
      '@type': 'Article',
      headline: workflow.h1,
      description: workflow.description,
      datePublished: workflow.publishedAt,
      dateModified: workflow.lastReviewed,
      author: { '@type': 'Organization', name: AUTHOR },
      publisher: {
        '@type': 'Organization',
        name: AUTHOR,
        parentOrganization: { '@type': 'Organization', name: OPERATOR_NAME },
      },
      mainEntityOfPage: absoluteUrl(pathname),
      image: SHARE_IMAGE,
      citation: workflowCitations(workflow.sources),
    })
  } else if (document.kind === 'article' && document.publishedAt) {
    graph.push({
      '@type': 'Article',
      headline: document.title,
      description: document.description,
      datePublished: document.publishedAt,
      author: { '@type': 'Organization', name: AUTHOR },
      publisher: { '@id': `${SITE}/#organization` },
      mainEntityOfPage: absoluteUrl(pathname),
      image: SHARE_IMAGE,
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

export function buildLlmsTxt(): string {
  const pages = sitemapDocuments()
    .map((document) => `- [${document.title}](${absoluteUrl(document.path)}): ${document.description}`)
    .join('\n')
  return [
    '# AZOA',
    '',
    `> ${DEFAULT_DESCRIPTION} ${SIMULATION} Written by ${AUTHOR}. Operated by ${OPERATOR_NAME}, Boise, Idaho. Email: ${OPERATOR_EMAIL}.`,
    '',
    '## Public pages',
    pages,
    '',
    '## Discovery',
    `- [Sitemap](${absoluteUrl('/sitemap.xml')}): Indexable public URLs.`,
    `- [Robots](${absoluteUrl('/robots.txt')}): Allows / and disallows account, operator, dashboard, and API routes.`,
    `- [RSS](${absoluteUrl('/feed.xml')}): Notes and workflow pages.`,
    '',
    '## Empty shelf',
    `- [News](${absoluteUrl('/news')}): No dated announcements are posted. This URL is not in the sitemap.`,
    '',
    'Operator: Escherbridge, LLC, Boise, Idaho.',
    `Contact: ${OPERATOR_EMAIL}`,
    '',
  ].join('\n')
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
  return `<?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>AZOA</title><link>${SITE}</link><description>${escapeXml(DEFAULT_DESCRIPTION)}</description>${items}</channel></rss>`
}
