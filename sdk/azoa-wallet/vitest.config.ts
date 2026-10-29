import { defineConfig } from "vitest/config";

export default defineConfig({
  // React tests opt into jsdom per file (`// @vitest-environment jsdom`); the automatic runtime matches tsconfig `jsx: react-jsx`.
  esbuild: { jsx: "automatic" },
  test: {
    globals: true,
    environment: "node",
    coverage: {
      provider: "v8",
      thresholds: { lines: 80, functions: 80, branches: 80 },
      include: ["src/**/*.{ts,tsx}"],
      exclude: ["src/**/*.test.ts", "src/**/index.ts"],
    },
  },
});
