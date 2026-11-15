import { describe, expect, it } from 'vitest'
import { migrateLegacyMoneyKeys } from './migrate-money-keys'

function fixture() {
  const data = new Map<string, string>()
  const source = new Map<string, string>()
  return { data, source, target: { read: (key: string) => data.get(key) ?? null, write: (key: string, value: string) => { data.set(key, value) }, remove: (key: string) => { data.delete(key) } },
    storage: { getItem: (key: string) => source.get(key) ?? null, removeItem: (key: string) => { source.delete(key) } } }
}
describe('money storage cutover', () => {
  it('preserves a lost-answer request and its server id across the SDK cutover', () => {
    const f = fixture(); const raw = JSON.stringify({ requestKey: 'stable', amountMinorUnits: 1200, serverId: 'intent' })
    f.source.set('azoa:funds:request:person', raw)
    migrateLegacyMoneyKeys(f.storage, f.target, 'person', 'checkout', 'cashOut')
    expect(f.data.get('checkout')).toBe(raw)
    expect(f.source.size).toBe(0)
  })
  it('refuses to choose between different unresolved keys', () => {
    const f = fixture(); const raw = JSON.stringify({ requestKey: 'lost', amountMinorUnits: 1200 })
    f.source.set('azoa:cash-out:request:person', raw)
    f.data.set('cashOut', JSON.stringify({ requestKey: 'other', amountMinorUnits: 1200 }))
    expect(() => migrateLegacyMoneyKeys(f.storage, f.target, 'person', 'checkout', 'cashOut')).toThrow('Two unresolved')
    expect(f.source.get('azoa:cash-out:request:person')).toBe(raw)
  })
  it('preserves the legacy evidence when the destination fails the SDK strict parser', () => {
    const f = fixture(); const raw = JSON.stringify({ requestKey: 'lost', amountMinorUnits: 1200 })
    f.source.set('azoa:cash-out:request:person', raw)
    f.data.set('cashOut', JSON.stringify({ requestKey: 'lost', amountMinorUnits: 1200, needsReview: false }))
    expect(() => migrateLegacyMoneyKeys(f.storage, f.target, 'person', 'checkout', 'cashOut')).toThrow()
    expect(f.source.get('azoa:cash-out:request:person')).toBe(raw)
  })
  it('preserves conflicting known server identities even for the same key and amount', () => {
    const f = fixture(); const raw = JSON.stringify({ requestKey: 'lost', amountMinorUnits: 1200, serverId: 'intent-one' })
    f.source.set('azoa:funds:request:person', raw)
    f.data.set('checkout', JSON.stringify({ requestKey: 'lost', amountMinorUnits: 1200, serverId: 'intent-two' }))
    expect(() => migrateLegacyMoneyKeys(f.storage, f.target, 'person', 'checkout', 'cashOut')).toThrow('Two unresolved')
    expect(f.source.get('azoa:funds:request:person')).toBe(raw)
  })
})
