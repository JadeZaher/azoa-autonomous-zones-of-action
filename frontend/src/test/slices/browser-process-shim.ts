/**
 * Next inlines `process.env.*` at build time; in Chromium there is no `process` at all. App
 * modules read `process.env.NEXT_PUBLIC_*` at import, so unset keys must read as `undefined`
 * here exactly as they do in a Next build. Imported first by `setup-browser.ts`.
 */
const globalScope = globalThis as unknown as { process?: { env: Record<string, string | undefined> } }
globalScope.process ??= { env: { NODE_ENV: 'test' } }

export {}
