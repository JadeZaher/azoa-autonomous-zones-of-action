<!-- BEGIN:nextjs-agent-rules -->

# This is NOT the Next.js you know

This version has breaking changes — APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` (resolved from this file's directory; in monorepos the `next` package may not be visible from the repo root) before writing any code. Heed deprecation notices.

This block is written and re-added by `next dev` — verify at `node_modules/next/dist/server/lib/generate-agent-files.js`. Removing it from a diff only re-creates the uncommitted change; committing it with your work keeps the tree clean.

<!-- END:nextjs-agent-rules -->

## Local dependencies and lint scope

Build the SDK with its own lockfile installation before installing frontend dependencies. Do not borrow an SDK `node_modules` junction: Turbopack follows the sibling SDK path, and a borrowed dependency tree can resolve outside this checkout even when TypeScript succeeds. The SDK's pinned `@msgpack/msgpack` remains its ordinary runtime dependency; no frontend shim or duplicate dependency is needed.

The custom React/Hook rule overrides use the same file extensions as Next's named `next` flat-config entry. Unscoped overrides also applied to `.cjs` configuration files that do not register those plugins, causing ESLint configuration failure before source linting. Matching the plugin scope preserves all Hook rules on the source files they cover.
