import { describe, expect, it } from 'vitest'
import { blocksText, indexBodyText, SIMULATION_SENTENCE, WORKFLOW_INDEX, workflowBodyText } from './workflows'
import {
  absoluteUrl,
  buildFeedXml,
  ESSAYS,
  injectsRuntimeApi,
  jsonLdGraph,
  organizationJsonLd,
  sitemapDocuments,
  WORKFLOWS,
} from './public-documents'

describe('public documents', () => {
  it('publishes marketing, notes, blog, and workflow urls on the custom host', () => {
    const urls = sitemapDocuments().map((document) => absoluteUrl(document.path))
    for (const path of ['/', '/architecture', '/federation', '/privacy', '/terms', '/blog', '/notes', '/workflows']) {
      expect(urls).toContain(absoluteUrl(path))
    }
    for (const essay of ESSAYS) expect(urls).toContain(absoluteUrl(`/notes/${essay.slug}`))
    for (const workflow of WORKFLOWS) expect(urls).toContain(absoluteUrl(`/workflows/${workflow.slug}`))
    expect(urls).not.toContain(absoluteUrl('/news'))
    expect(urls).not.toContain(absoluteUrl('/login'))
    expect(urls.join('\n')).not.toContain('railway.app')
    expect(urls.join('\n')).not.toContain('azoa.io')
    expect(urls.join('\n')).not.toContain('/guides')
  })

  it('builds an RSS feed from the essays and workflow pages', () => {
    const xml = buildFeedXml()
    expect(xml.startsWith('<?xml')).toBe(true)
    expect(xml).toContain('<rss version="2.0">')
    expect(xml).toContain('/notes/settle-once-or-not-at-all')
    expect(xml).toContain('/workflows/consent-records')
    expect(xml).toContain('/workflows/settle-once')
    expect(xml).toContain('/workflows/fail-closed')
    expect(xml).toContain('/workflows/idempotent-requests')
    expect(xml).toContain('/workflows/consent-withdrawal')
    expect(xml).toContain('/workflows/least-privilege')
    expect(xml).toContain('/workflows/separation-of-duties')
    expect(xml).toContain('/workflows/event-logging')
    expect(xml).toContain('/workflows/workflow-state-machines')
    expect(xml).toContain('/workflows/compensating-transactions')
    expect(xml).toContain('/workflows/safe-and-idempotent')
    expect(xml).toContain('/workflows/demonstrable-consent')
    expect(xml).toContain('/workflows/distinguishable-consent')
    expect(xml).not.toContain('/workflows/action-graphs')
    expect(xml).not.toContain('/workflows/grants-and-bounties')
    expect(xml).not.toContain('railway.app')
    expect(xml).not.toContain('never double-spend')
    expect(xml).toContain('not a KYC feature')
    expect(xml.toLowerCase()).not.toContain('kyc is available')
    expect(xml).not.toContain('FIAT-BRIDGED')
  })

  it('keeps the runtime API url off public html and on the sign-in route', () => {
    expect(injectsRuntimeApi('/')).toBe(false)
    expect(injectsRuntimeApi('/architecture')).toBe(false)
    expect(injectsRuntimeApi('/workflows/settle-once')).toBe(false)
    expect(injectsRuntimeApi('/login')).toBe(true)
    expect(injectsRuntimeApi(null)).toBe(true)
  })

  it('publishes a city-level operator address', () => {
    expect(organizationJsonLd().parentOrganization.address).toEqual({
      '@type': 'PostalAddress',
      addressLocality: 'Boise',
      addressRegion: 'ID',
      addressCountry: 'US',
    })
  })

  it('states the simulation on the index and every workflow, with the cited sources', () => {
    expect(ESSAYS).toHaveLength(2)
    expect(WORKFLOWS.map((workflow) => workflow.slug)).toEqual([
      'settle-once',
      'fail-closed',
      'consent-records',
      'idempotent-requests',
      'consent-withdrawal',
      'least-privilege',
      'separation-of-duties',
      'event-logging',
      'workflow-state-machines',
      'compensating-transactions',
      'safe-and-idempotent',
      'demonstrable-consent',
      'distinguishable-consent',
    ])
    const index = indexBodyText()
    expect(index).toContain('Shapes the engine can describe.')
    expect(index).toContain(SIMULATION_SENTENCE)
    expect(blocksText(WORKFLOW_INDEX.blocks)).toContain(SIMULATION_SENTENCE)
    expect(index).toContain('https://ardanova.com')
    expect(index).toContain('/notes/settle-once-or-not-at-all')
    expect(index).toContain('/notes/fail-closed-or-it-isnt-a-control')
    expect(index).toContain('/workflows/idempotent-requests')
    expect(index).toContain('/workflows/consent-withdrawal')
    expect(index).toContain('/workflows/least-privilege')
    expect(index).toContain('/workflows/separation-of-duties')
    expect(index).toContain('/workflows/event-logging')
    expect(index).toContain('/workflows/workflow-state-machines')
    expect(index).toContain('/workflows/compensating-transactions')
    expect(index).toContain('/workflows/safe-and-idempotent')
    expect(index).toContain('/workflows/demonstrable-consent')
    expect(index).toContain('/workflows/distinguishable-consent')
    expect(index).not.toContain('azoa.io')
    expect(index).not.toContain('/patterns')

    for (const workflow of WORKFLOWS) {
      const text = workflowBodyText(workflow)
      expect(text).toContain(workflow.h1)
      expect(text).toContain(SIMULATION_SENTENCE)
      expect(blocksText(workflow.blocks, workflow.sources)).toContain(SIMULATION_SENTENCE)
      expect(text).toContain('Sources')
      expect(text).toContain('An illustrative scenario')
      expect(text).toContain('https://ardanova.com')
      expect(text).not.toContain('azoa.io')
      expect(text).not.toContain('ardanova.com/patterns')
      expect(text).not.toContain('never double-spend')
      expect(text).not.toContain('FIAT-BRIDGED')
      expect(text.toLowerCase()).not.toContain('kyc is available')
      expect(text.toLowerCase()).not.toContain('live custody is')
      for (const source of workflow.sources) {
        for (const url of source.urls) expect(text).toContain(url)
      }
    }

    const settlePage = WORKFLOWS[0]!
    expect(settlePage.description).toBe('A retried step should settle once, or not at all. Stripe API v1 and FedNow already name that habit. This hosted site does not move money.')
    const settle = workflowBodyText(settlePage)
    expect(settle).toContain('including 500 errors')
    expect(settle).toContain('retries don’t repeat successful operations')
    expect(settle).toContain('admi.002')
    expect(settle).toContain('pacs.028')
    expect(settle).toContain('Version 3.2 (June 2025)')

    const closedPage = WORKFLOWS[1]!
    expect(closedPage.description).toBe('Fail-closed means permission, not exclusion, as Saltzer and Schroeder wrote in 1975. A warning in a log is not a control. This hosted site does not move value.')
    const closed = workflowBodyText(closedPage)
    expect(closed).toContain('Base access decisions on permission rather than exclusion.')
    expect(closed).toContain('11 October 1974')
    expect(closed).toContain('17 April 1975')
    expect(closed).toContain('E. Glaser in 1965')
    expect(closed).toContain('Every access to every object must be checked for authority')

    const consent = workflowBodyText(WORKFLOWS[2]!)
    expect(consent).toContain('as easy to withdraw as to give consent')
    expect(consent).toContain('clearly distinguishable from the other matters')
    expect(consent).toContain('utmost account shall be taken')
    expect(consent).toContain('reasoned-objection check in ACT')
    expect(consent).toContain('https://azoa.ardanova.com/privacy')
    expect(consent).toContain('could not be retrieved in the research pass (403)')
    expect(consent).toContain('does not paraphrase ICO audit-trail field lists from memory')
  })

  it('keeps the fact-checked figures and limits on the five field notes', () => {
    const bySlug = Object.fromEntries(WORKFLOWS.map((workflow) => [workflow.slug, workflow]))
    const idempotent = workflowBodyText(bySlug['idempotent-requests']!)
    expect(bySlug['idempotent-requests']!.seoTitle).toBe('Idempotent retries in a workflow — Workflows | AZOA')
    expect(idempotent).toContain('A request method is considered \'idempotent\' if the intended effect on the server of multiple identical requests with that method is the same as the effect for a single such request.')
    expect(idempotent).toContain('PUT, DELETE, and safe request methods are idempotent.')
    expect(idempotent).toContain('A proxy MUST NOT automatically retry non-idempotent requests.')
    expect(idempotent).toContain('A client SHOULD NOT automatically retry a failed automatic retry.')
    expect(idempotent).toContain('https://www.rfc-editor.org/rfc/rfc9110.html')
    expect(idempotent).toContain('https://primusneo.com/guides/idempotent-requests')
    expect(idempotent).toContain('does not invent "idempotency keys"')
    expect(idempotent).toContain('The RFC does not say that POST is idempotent')
    expect(idempotent).toContain('An illustrative scenario')

    const withdrawal = workflowBodyText(bySlug['consent-withdrawal']!)
    expect(withdrawal).toContain('not legal advice')
    expect(withdrawal).toContain('It shall be as easy to withdraw as to give consent.')
    expect(withdrawal).toContain('clearly distinguishable from the other matters')
    expect(withdrawal).toContain('utmost account shall be taken')
    expect(withdrawal).toContain('https://www.legislation.gov.uk/eur/2016/679/article/7')
    expect(withdrawal).toContain('does not enable identity checks for moving real money')
    expect(withdrawal).toContain('An illustrative scenario')

    const least = workflowBodyText(bySlug['least-privilege']!)
    expect(least).toContain('Employ the principle of least privilege, allowing only authorized accesses for users (or processes acting on behalf of users) that are necessary to accomplish assigned organizational tasks.')
    expect(least).toContain('Organizations apply least privilege to the development, implementation, and operation of organizational systems.')
    expect(least).toContain('https://csrc.nist.gov/publications/detail/sp/800-53/rev-5/final')
    expect(least).toContain('does not claim to implement 800-53')
    expect(least).toContain('no exploit steps')
    expect(least).toContain('An illustrative scenario')

    const duties = workflowBodyText(bySlug['separation-of-duties']!)
    expect(duties).toContain('Identify and document [Assignment: organization-defined duties of individuals requiring separation]')
    expect(duties).toContain('Define system access authorizations to support separation of duties.')
    expect(duties).toContain('does not invent a mandatory initiator/approver pair')
    expect(duties).toContain('does not claim to implement 800-53')
    expect(duties).toContain('It does not move value.')
    expect(duties).toContain('An illustrative scenario')

    const events = workflowBodyText(bySlug['event-logging']!)
    expect(events).toContain('Identify the types of events that the system is capable of logging in support of the audit function: [Assignment: organization-defined event types that the system is capable of logging].')
    expect(events).toContain('They are not reconstructed here.')
    expect(events).toContain('is not cited as quoted text.')
    expect(events).toContain('does not claim to implement 800-53')
    expect(events).toContain('no keyloggers, clipboard monitors, secret capture, or exploit steps')
    expect(events).toContain('An illustrative scenario')

    for (const slug of ['least-privilege', 'separation-of-duties', 'event-logging']) {
      const text = workflowBodyText(bySlug[slug]!)
      expect(text).toContain('https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-53r5.pdf')
      expect(text).toContain('https://doi.org/10.6028/NIST.SP.800-53r5')
    }

    const bpmn = workflowBodyText(bySlug['workflow-state-machines']!)
    expect(bpmn).toContain('Business Process Model and Notation has become the de-facto standard for business processes diagrams.')
    expect(bpmn).toContain('The primary goal of BPMN is to provide a notation that is readily understandable by all business users')
    expect(bpmn).toContain('NOTE: Version 2.0.2 contains a minor change to Clause 15.')
    expect(bpmn).toContain('cannot claim compliance or conformance')
    expect(bpmn).toContain('does not claim AZOA conformance')
    expect(bpmn).toContain('https://www.omg.org/spec/BPMN')
    expect(bpmn).toContain('https://www.omg.org/spec/BPMN/2.0.2/PDF')
    expect(bpmn).toContain('workflowpatterns.com did not open (HTTP 500 / failed fetch) on 2026-11-10')
    expect(bpmn).toContain('An illustrative scenario')

    const saga = workflowBodyText(bySlug['compensating-transactions']!)
    expect(saga).toContain('The database management system guarantees that either all the transactions in a saga are successfully completed or compensating transactions are run to amend a partial execution.')
    expect(saga).toContain('https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf')
    expect(saga).toContain('not a recovery playbook')
    expect(saga).toContain('does not write implementation steps')
    expect(saga).toContain('An illustrative scenario')

    const safe = workflowBodyText(bySlug['safe-and-idempotent']!)
    expect(safe).toContain('Request methods are considered ‘safe’ if their defined semantics are essentially read-only')
    expect(safe).toContain('GET, HEAD, OPTIONS, and TRACE methods are defined to be safe.')
    expect(safe).toContain('A request method is considered ‘idempotent’ if the intended effect on the server of multiple identical requests with that method is the same as the effect for a single such request.')
    expect(safe).toContain('PUT and DELETE are idempotent without being safe.')
    expect(safe).toContain('idempotent… or some means to detect that the original request was never applied.')
    expect(safe).toContain('https://www.rfc-editor.org/rfc/rfc9110.html')
    expect(safe).toContain('https://primusneo.com/guides/idempotent-requests')
    expect(safe).toContain('no attack recipes')
    expect(safe).toContain('An illustrative scenario')

    const show = workflowBodyText(bySlug['demonstrable-consent']!)
    expect(show).toContain('Where processing is based on consent, the controller shall be able to demonstrate that the data subject has consented to processing of his or her personal data.')
    expect(show).toContain('not legal advice')
    expect(show).toContain('https://www.legislation.gov.uk/eur/2016/679/article/7')
    expect(show).toContain('An illustrative scenario')

    const distinct = workflowBodyText(bySlug['distinguishable-consent']!)
    expect(distinct).toContain('If the data subject\'s consent is given in the context of a written declaration which also concerns other matters, the request for consent shall be presented in a manner which is clearly distinguishable from the other matters, in an intelligible and easily accessible form, using clear and plain language. Any part of such a declaration which constitutes an infringement of this Regulation shall not be binding.')
    expect(distinct).toContain('When assessing whether consent is freely given, utmost account shall be taken of whether, inter alia, the performance of a contract, including the provision of a service, is conditional on consent to the processing of personal data that is not necessary for the performance of that contract.')
    expect(distinct).toContain('The second sentence is not abridged.')
    expect(distinct).toContain('not legal advice')
    expect(distinct).toContain('An illustrative scenario')
  })

  it('puts article, breadcrumb, and collection JSON-LD on the workflow routes', () => {
    const index = JSON.stringify(jsonLdGraph('/workflows'))
    expect(index).toContain('"@type":"CollectionPage"')
    expect(index).toContain('"@type":"ItemList"')
    expect(index).toContain('"@type":"BreadcrumbList"')
    expect(index).toContain('"name":"Workflows"')
    expect(index).toContain('"name":"Escherbridge"')
    expect(index).toContain('Escherbridge, LLC')
    for (const workflow of WORKFLOWS) {
      expect(index).toContain(absoluteUrl(`/workflows/${workflow.slug}`))
      const article = JSON.stringify(jsonLdGraph(`/workflows/${workflow.slug}`))
      expect(article).toContain('"@type":"Article"')
      expect(article).toContain('"@type":"BreadcrumbList"')
      expect(article).toContain(`"name":"${workflow.title}"`)
      expect(article).toContain(workflow.h1)
      expect(article).toContain('"datePublished":"2026-11-10"')
      expect(article).toContain('"dateModified":"2026-11-10"')
      expect(article).toContain(`"mainEntityOfPage":"${absoluteUrl(`/workflows/${workflow.slug}`)}"`)
      expect(article).toContain('Escherbridge, LLC')
      for (const source of workflow.sources) {
        expect(article).toContain(source.name)
        for (const url of source.urls) expect(article).toContain(url)
      }
    }
    const consentLd = JSON.stringify(jsonLdGraph('/workflows/consent-records'))
    expect(consentLd).not.toContain('/privacy')
    expect(index).not.toContain('action-graphs')
    expect(index).not.toContain('grants-and-bounties')
  })
})
