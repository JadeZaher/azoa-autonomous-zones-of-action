import { http, HttpResponse, type HttpHandler } from 'msw'
import type {
  WebhookRegistrationCommand,
  WebhookRegistrationMutationResult,
  WebhookRegistrationStatus,
} from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { fresh } from '../fixtures'
import { requireBearer } from './auth'

/** A fake of `WebhookRegistrationController` with the tenant's registration as state. */
export interface WebhookRegistrationApi {
  handlers: HttpHandler[]
  readonly current: WebhookRegistrationStatus | null
  /** The next PUT commits, then the response is lost on the way back (network error). */
  loseNextResponse(): void
}

/**
 * Semantics follow `Providers/Stores/Surreal/SurrealWebhookRegistrationStore.Management.cs`:
 * version compare-and-swap, an exact replay of a committed request returns its receipt with
 * `replayed: true` and never re-discloses the secret, and a withdrawn request can never apply.
 */
export function webhookRegistrationApi(
  options: { tenantId: string; initial?: WebhookRegistrationStatus | null },
): WebhookRegistrationApi {
  let current: WebhookRegistrationStatus | null = options.initial ? fresh(options.initial) : null
  const committed = new Map<string, { body: string; result: WebhookRegistrationMutationResult }>()
  const withdrawn = new Set<string>()
  let responsesToLose = 0

  const conflict = (message: string) => HttpResponse.json(azoaError(message), { status: 409 })

  const handlers = [
    http.get(apiUrl('/api/tenant/webhook-registration'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk(current, current ? 'Success' : 'No registration.')),
    ),
    http.put(apiUrl('/api/tenant/webhook-registration'), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const body = await request.text()
      const command = JSON.parse(body) as WebhookRegistrationCommand

      // The store hashes the whole command: the same request id with a different command is refused.
      const prior = committed.get(command.requestId)
      if (prior)
        return prior.body === body
          ? HttpResponse.json(azoaOk({ ...prior.result, newSecret: null, replayed: true }))
          : conflict('This request id was already used for a different command.')
      if (withdrawn.has(command.requestId)) return conflict('This request was withdrawn and can never apply.')
      if (command.reason.trim().length < 10 || command.reason.trim().length > 1000)
        return conflict('An exact request, registration/version and 10-1000 character reason are required.')
      if ((current?.version ?? 0) !== command.expectedVersion || (current && current.registrationId !== command.registrationId))
        return conflict('Registration changed or needs explicit legacy replacement.')
      if (!command.url.startsWith('https://')) return conflict('The webhook destination is not permitted.')

      const now = new Date().toISOString()
      const newSecret = !current || command.rotateSecret ? `whsig-${crypto.randomUUID()}` : null
      const next: WebhookRegistrationStatus = {
        registrationId: command.registrationId,
        tenantId: options.tenantId,
        version: command.expectedVersion + 1,
        destinationRevision: current ? current.destinationRevision + (current.url === command.url ? 0 : 1) : 1,
        secretRevision: current ? current.secretRevision + (newSecret ? 1 : 0) : 1,
        url: command.url,
        isActive: command.isActive,
        createdAt: current?.createdAt ?? now,
        secretRotatedAt: current && newSecret ? now : current?.secretRotatedAt ?? null,
        legacyFingerprint: null,
      }
      const result: WebhookRegistrationMutationResult = {
        receipt: {
          requestId: command.requestId,
          registrationId: next.registrationId,
          tenantId: next.tenantId,
          version: next.version,
          destinationRevision: next.destinationRevision,
          secretRevision: next.secretRevision,
          url: next.url,
          isActive: next.isActive,
          recordedAt: now,
        },
        newSecret,
        replayed: false,
      }
      current = next
      committed.set(command.requestId, { body, result })

      if (responsesToLose > 0) {
        responsesToLose -= 1
        return HttpResponse.error()
      }
      return HttpResponse.json(azoaOk(result))
    }),
    http.post(apiUrl('/api/tenant/webhook-registration/withdraw'), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const command = (await request.json()) as WebhookRegistrationCommand
      const applied = committed.get(command.requestId)
      if (!applied) withdrawn.add(command.requestId)
      return HttpResponse.json(
        azoaOk({ requestId: command.requestId, alreadyApplied: Boolean(applied), receipt: applied?.result.receipt ?? null }),
      )
    }),
  ]

  return {
    handlers,
    get current() {
      return current
    },
    loseNextResponse() {
      responsesToLose += 1
    },
  }
}
