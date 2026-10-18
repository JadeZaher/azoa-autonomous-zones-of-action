/// <reference types="vitest" />
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
import { playwright } from '@vitest/browser-playwright'
import { configDefaults, defineConfig } from 'vitest/config'

/**
 * Three Vitest projects. Run `npm test` (unit + slices on jsdom) or `npm run test:browser`
 * (slices in real Chromium). See `src/test/AGENTS.md` for what each layer proves, and
 * `src/components/action-builder/AGENTS.md` §testing for the original jsdom harness.
 */
const sliceTests = 'src/test/slices/**/*.slice.test.{ts,tsx}'

/**
 * `operator-console` imports the real Next.js route handler (`app/api/operator/[...path]/route`)
 * to exercise the BFF layer (see `src/test/AGENTS.md` §slices) — that pulls in `next/server`,
 * whose `ua-parser-js` dependency reads `__dirname`, a Node global a real Chromium page does not
 * have. It is Node-only by design, not a browser surface; keep it on the jsdom `slices` project
 * and drop it from `slices-browser` instead of letting the whole file fail to import there.
 */
const browserIncompatibleSliceTests = ['src/test/slices/operator-console.slice.test.tsx']

/**
 * Slices run against the SDK SOURCE, not the `file:` dependency's `dist/` (which is only as
 * fresh as the last `tsup` build): drift between the SDK and the API must be visible without a
 * rebuild step. Exact-match patterns keep `azoa-sdk/api` from being rewritten into a file path.
 */
const sliceAliases = [
  { find: /^azoa-sdk$/, replacement: fileURLToPath(new URL('../sdk/azoa-wallet/src/index.ts', import.meta.url)) },
  { find: /^azoa-sdk\/api$/, replacement: fileURLToPath(new URL('../sdk/azoa-wallet/src/api/index.ts', import.meta.url)) },
]

export default defineConfig({
  plugins: [react()],
  resolve: {
    // Mirrors the `@/*` path alias in tsconfig.json.
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  test: {
    // Stylesheets are imported for their side effects only (`@xyflow/react/dist/style.css`);
    // parsing them would cost time and tell the assertions nothing.
    css: false,
    restoreMocks: true,
    // React Flow mounts a lot of DOM per test; a longer default keeps CI honest on cold starts.
    testTimeout: 15_000,
    projects: [
      {
        extends: true,
        test: {
          name: 'unit',
          environment: 'jsdom',
          setupFiles: ['./src/test/setup.ts'],
          include: ['src/**/*.test.{ts,tsx}'],
          exclude: [...configDefaults.exclude, sliceTests],
        },
      },
      {
        extends: true,
        resolve: { alias: sliceAliases },
        test: {
          name: 'slices',
          environment: 'jsdom',
          setupFiles: ['./src/test/setup.ts', './src/test/slices/setup-jsdom.ts'],
          include: [sliceTests],
        },
      },
      {
        extends: true,
        resolve: { alias: sliceAliases },
        // Serves mockServiceWorker.js at "/" for the MSW worker without shipping it in `public/`.
        publicDir: fileURLToPath(new URL('./src/test/msw/public', import.meta.url)),
        // The SDK source lives outside this package root.
        server: { fs: { allow: ['..'] } },
        test: {
          name: 'slices-browser',
          include: [sliceTests],
          exclude: [...configDefaults.exclude, ...browserIncompatibleSliceTests],
          setupFiles: ['./src/test/slices/setup-browser.ts'],
          browser: {
            enabled: true,
            provider: playwright(),
            headless: true,
            instances: [{ browser: 'chromium' }],
          },
        },
      },
    ],
  },
})
