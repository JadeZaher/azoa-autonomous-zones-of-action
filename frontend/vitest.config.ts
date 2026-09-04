/// <reference types="vitest" />
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

/**
 * Component test runner for `frontend/`. Run with `npm test` (watch: `npm run test:watch`).
 * See `src/components/quest-builder/AGENTS.md` §testing for what the harness covers and why.
 */
export default defineConfig({
  plugins: [react()],
  resolve: {
    // Mirrors the `@/*` path alias in tsconfig.json.
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // Stylesheets are imported for their side effects only (`@xyflow/react/dist/style.css`);
    // parsing them would cost time and tell the assertions nothing.
    css: false,
    restoreMocks: true,
    // React Flow mounts a lot of DOM per test; a longer default keeps CI honest on cold starts.
    testTimeout: 15_000,
  },
})
