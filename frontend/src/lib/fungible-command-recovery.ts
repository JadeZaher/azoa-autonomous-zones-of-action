/** Proves that an uncommitted command's exact version can no longer win its CAS. */
export function isFungibleCommandSuperseded(
  command: { operationId: string; version: number },
  observed: { operationId: string; version: number },
): boolean {
  return command.operationId === observed.operationId
    && Number.isSafeInteger(command.version) && command.version >= 0
    && Number.isSafeInteger(observed.version) && observed.version > command.version
}
