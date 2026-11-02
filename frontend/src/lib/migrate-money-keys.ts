import { readSavedRequest, type KeyStore } from 'azoa-sdk/money'

/** Moves a legacy unresolved request without changing its identity; conflicting evidence blocks new sends. */
export function migrateLegacyMoneyKeys(storage: Pick<Storage, 'getItem' | 'removeItem'>, target: KeyStore, subject: string, checkoutScope: string, cashOutScope: string) {
  for (const [legacyScope, scope] of [['funds', checkoutScope], ['cash-out', cashOutScope]]) {
    const key = `azoa:${legacyScope}:request:${subject}`
    const raw = storage.getItem(key)
    if (raw === null) continue
    const saved = readSavedRequest({ ...target, read: () => raw }, scope!, true)!
    const current = target.read(scope!)
    if (current !== null) {
      const parsed = readSavedRequest(target, scope!, true)!
      if (parsed.requestKey !== saved.requestKey || parsed.amountMinorUnits !== saved.amountMinorUnits
        || (saved.serverId !== undefined && parsed.serverId !== saved.serverId)
        || (saved.reference !== undefined && parsed.reference !== saved.reference)
        || (saved.needsReview && !parsed.needsReview))
        throw new Error('Two unresolved payment identities need reconciliation.')
    } else target.write(scope!, raw)
    const restored = readSavedRequest(target, scope!, true)
    if (restored?.requestKey !== saved.requestKey || restored.amountMinorUnits !== saved.amountMinorUnits) throw new Error('Payment identity could not be saved.')
    storage.removeItem(key)
  }
}
