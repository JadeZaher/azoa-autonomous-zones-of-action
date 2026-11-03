import assert from 'node:assert/strict'
import test from 'node:test'
import { assertDatabaseVolumeContract } from './database-volume-contract.mjs'

const owner = 'a'.repeat(32)
const contract = 'b'.repeat(64)
const labels = (hash) => ({ 'com.azoa.e2e.owner': owner, ...(hash === undefined ? {} : { 'com.azoa.e2e.surreal-contract': hash }) })

test('the current contract permits both startup actions on an owned volume', () => {
  for (const action of ['prepare', 'up'])
    assert.doesNotThrow(() => assertDatabaseVolumeContract(labels(contract), owner, contract, action))
})

test('an unlabelled old owned volume cannot reach either startup action', () => {
  for (const action of ['prepare', 'up'])
    assert.throws(() => assertDatabaseVolumeContract(labels(), owner, contract, action), /no matching release contract/)
})

test('a different contract cannot reach either startup action', () => {
  for (const action of ['prepare', 'up'])
    assert.throws(() => assertDatabaseVolumeContract(labels('c'.repeat(64)), owner, contract, action), /no matching release contract/)
})

test('owned old volumes remain available to cleanup without a compatibility bypass for startup', () => {
  for (const hash of [undefined, 'c'.repeat(64)])
    for (const action of ['down', 'logs'])
      assert.doesNotThrow(() => assertDatabaseVolumeContract(labels(hash), owner, contract, action))
})

test('cleanup and startup both refuse unknown ownership', () => {
  for (const action of ['prepare', 'up', 'down', 'logs']) {
    assert.throws(() => assertDatabaseVolumeContract(labels(contract), 'd'.repeat(32), contract, action), /unknown/)
    assert.throws(() => assertDatabaseVolumeContract({}, owner, contract, action), /unknown/)
  }
})

test('invalid actions and missing canonical startup hashes fail closed', () => {
  assert.throws(() => assertDatabaseVolumeContract(labels(contract), owner, contract, 'wipe'), /Unsupported/)
  assert.throws(() => assertDatabaseVolumeContract(labels(), owner, undefined, 'up'), /no matching/)
})
