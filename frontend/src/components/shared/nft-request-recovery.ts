import type { NftLifecycleView, NftOperation } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'

/** Clears only saved requests authoritatively correlated to this terminal operation. */
export async function clearResolvedNftRequests(userId: string, value: NftLifecycleView): Promise<boolean> {
  if (!['Completed', 'Cancelled', 'Failed'].includes(value.state)) return false
  let cleared = false
  const keys = Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index)).filter((key): key is string => key !== null)
  for (const storageKey of keys) {
    const mint = storageKey === `azoa:nft-mint:${userId}`
    const move = storageKey.startsWith(`azoa:nft-move:${userId}:`)
    const refund = storageKey.startsWith(`azoa:nft-refund:${userId}:`)
    if (!mint && !move && !refund) continue
    const raw = localStorage.getItem(storageKey)
    if (!raw) continue
    let saved: { key: string; kind?: string; operationId?: string }
    try { saved = JSON.parse(raw); if (typeof saved.key !== 'string') continue } catch { continue }
    const operation: NftOperation = mint ? 'Mint' : refund ? 'Refund' : saved.kind === 'burn' ? 'Burn' : 'Transfer'
    if (operation !== value.operation) continue
    let matches = saved.operationId === value.operationId
    if (!saved.operationId) {
      const found = await azoa.api.getNftLifecycleByRequest(operation, saved.key)
      matches = isOk(found) && found.value.operationId === value.operationId && ['Completed', 'Cancelled', 'Failed'].includes(found.value.state)
    }
    if (matches && localStorage.getItem(storageKey) === raw) { localStorage.removeItem(storageKey); cleared = true }
  }
  return cleared
}
