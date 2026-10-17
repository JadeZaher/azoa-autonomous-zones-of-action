import type { NftFeeQuote } from 'azoa-sdk'

export function NftFeeReview({ quote, authorization = true }: { quote: NftFeeQuote; authorization?: boolean }) {
  const raw = quote.nativeBaseUnits.padStart(quote.decimals + 1, '0')
  const amount = quote.decimals ? `${raw.slice(0, -quote.decimals)}.${raw.slice(-quote.decimals)}` : raw
  return <div className="rounded border p-3 text-sm space-y-1">
    <p>Platform fee: <strong>{amount} {quote.currency}</strong> on {quote.network}</p>
    <p>The initiating account pays this fee once for this operation. Chain network fees are additional.</p>
    {authorization ? <p>Continue only if you authorize this fee.</p> : <p>Acceptance or creator destruction may require the signing participant to pay their own chain network fee.</p>}
  </div>
}
