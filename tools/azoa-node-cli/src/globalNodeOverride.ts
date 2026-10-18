/**
 * Backing store for the global `--node` flag (cli.ts). Kept out of
 * context.ts's function signature so every command doesn't need to thread
 * an extra parameter through — set once per process invocation.
 */
let override: string | undefined;

export function setGlobalNodeOverride(node: string | undefined): void {
  override = node;
}

export function getGlobalNodeOverride(): string | undefined {
  return override;
}
