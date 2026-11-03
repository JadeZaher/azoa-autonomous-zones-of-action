// Checks owned test-volume compatibility; see scripts/AGENTS.md.
export function assertDatabaseVolumeContract(labels, ownerId, contractSha256, action) {
  if (typeof ownerId !== 'string' || !/^[a-f0-9]{32}$/.test(ownerId) || labels?.['com.azoa.e2e.owner'] !== ownerId)
    throw new Error('Refusing an unknown E2E database volume.')
  if (!['prepare', 'up', 'down', 'logs'].includes(action))
    throw new Error('Unsupported E2E database-volume action.')
  if (action === 'prepare' || action === 'up') {
    if (typeof contractSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(contractSha256)
      || labels['com.azoa.e2e.surreal-contract'] !== contractSha256)
      throw new Error('Owned E2E database volume has no matching release contract; use owner-scoped down --wipe before creating a fresh store.')
  }
}
