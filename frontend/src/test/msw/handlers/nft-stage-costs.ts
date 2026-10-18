import { http, HttpResponse, type HttpHandler } from 'msw'
import type { NftLifecycleView, NftProtocolCostLimits, NftProtocolCostOffer, NftStageAcceptance, NftStageCostAcceptance } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/**
 * A fake of `Controllers/NftStageCostsController.cs` + `UserNFTController` refresh for one recipient-stage Solana
 * lifecycle (F-M11, `Services/Users/NftLifecycleService.StageCosts.cs`): quote binds the exact reviewed stage; accept is
 * idempotent per stage request id; refresh refuses with NFT_STAGE_COSTS_CHANGED while costs exceed the accepted limits.
 */
export interface NftStageCostsApi {
  handlers: HttpHandler[]
  readonly view: NftLifecycleView
  /** Quote ids of every acceptance the node recorded, in order. */
  readonly acceptedQuoteIds: readonly string[]
  /** Current network costs rise above any acceptance made so far. */
  raiseCosts(): void
}

export function nftStageCostsApi(initial: NftLifecycleView): NftStageCostsApi {
  let view = initial
  let networkFee = 5_000
  let acceptedFee: number | null = null
  const offers = new Map<string, NftProtocolCostOffer>()
  const accepted = new Map<string, NftStageCostAcceptance>()
  const acceptedQuoteIds: string[] = []
  const reviews = (stage: NftStageAcceptance) => stage.requestId && stage.expectedVersion === view.version
    && stage.state === view.state && stage.stage === view.stage && stage.generation === view.preparationGeneration
  const limits = (): NftProtocolCostLimits => ({ networkFee: String(networkFee), retainedRent: '2039280', protocolLevy: '0' })
  const base = `/api/nft-stage-costs/${initial.operationId}`

  const handlers = [
    http.post(apiUrl(`${base}/quote`), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const stage = (await request.json()) as NftStageAcceptance
      if (!reviews(stage)) return HttpResponse.json(azoaError('Review the current unsigned NFT stage before requesting its costs.', 'OPERATION_NOT_ALLOWED'), { status: 400 })
      const quoteId = crypto.randomUUID().replaceAll('-', '')
      const now = new Date()
      const offer: NftProtocolCostOffer = {
        quote: {
          quoteId, actorUserId: view.stageCostPayerUserId!, actingTenantId: '00000000-0000-0000-0000-000000000000', payerWalletId: crypto.randomUUID(),
          payerAddress: 'PayerWa11et1111111111111111111111111111111', chainType: 'Solana', network: 'Devnet', physicalNetworkIdentity: 'solana:devnet',
          lifecycleId: view.operationId, draftHash: 'd'.repeat(64), stage: view.stage, preparationGeneration: view.preparationGeneration,
          tokenStandard: 'spl-token', policyEvidenceJson: '{}', limits: limits(), createdAt: now.toISOString(),
          expiresAt: new Date(now.getTime() + 300_000).toISOString(),
        },
        authorization: { quoteId, quoteHash: 'h'.repeat(64), limits: limits() },
      }
      offers.set(quoteId, offer)
      return HttpResponse.json(azoaOk(offer))
    }),
    http.post(apiUrl(`${base}/accept`), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const body = (await request.json()) as NftStageCostAcceptance
      const prior = accepted.get(body.stage.requestId)
      if (prior) return JSON.stringify(prior) === JSON.stringify(body) ? HttpResponse.json(azoaOk(view), { status: 202 })
        : HttpResponse.json(azoaError('The stage cost acceptance key belongs to a different request.', 'OPERATION_NOT_ALLOWED'), { status: 400 })
      const offer = offers.get(body.costs.quoteId)
      if (!reviews(body.stage) || !offer || offer.authorization.quoteHash !== body.costs.quoteHash)
        return HttpResponse.json(azoaError('The exact NFT stage cost offer is unavailable.', 'OPERATION_NOT_ALLOWED'), { status: 400 })
      accepted.set(body.stage.requestId, body)
      acceptedQuoteIds.push(body.costs.quoteId)
      acceptedFee = Number(body.costs.limits.networkFee)
      view = { ...view, version: view.version + 1, needsStageCosts: false, acceptedStageCostLimits: body.costs.limits }
      return HttpResponse.json(azoaOk(view), { status: 202 })
    }),
    http.post(apiUrl(`/api/usernft/lifecycle/${initial.operationId}/refresh`), ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      if (acceptedFee === null) return HttpResponse.json(azoaOk(view), { status: 202 })
      if (networkFee > acceptedFee)
        return HttpResponse.json(azoaError('NFT stage costs changed since acceptance; review and accept a new quote.', 'NFT_STAGE_COSTS_CHANGED'), { status: 400 })
      view = { ...view, state: 'Prepared', version: view.version + 1 }
      return HttpResponse.json(azoaOk(view), { status: 202 })
    }),
  ]

  return {
    handlers,
    get view() { return view },
    get acceptedQuoteIds() { return acceptedQuoteIds },
    raiseCosts() { networkFee = 9_000 },
  }
}
