import type { NftProtocolCostOffer } from 'azoa-sdk'

export function NftProtocolCostReview({ offer, accepted }: { offer: NftProtocolCostOffer; accepted: boolean }) {
  const { quote } = offer
  return <div className="rounded border p-3 text-sm space-y-1">
    <p>Creation costs on <strong>{quote.chainType} {quote.network}</strong></p>
    <p>Maximum network fee: <strong>{quote.limits.networkFee} native base units</strong></p>
    <p>Account rent retained: <strong>{quote.limits.retainedRent} native base units</strong></p>
    <p>Protocol levy spent: <strong>{quote.limits.protocolLevy} native base units</strong></p>
    <p>These are separate limits, additional to the platform fee. Retained rent is not a promise of a refund.</p>
    <p className="break-all">Payer wallet: {quote.payerAddress}</p>
    <p>{accepted ? 'Your accepted receipt is saved for this request and its retries.' : `Offer expires ${new Date(quote.expiresAt).toLocaleString()}. Continue only if you accept all three limits.`}</p>
  </div>
}
