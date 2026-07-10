import { defineConfig } from "tsup";

export default defineConfig({
  entry: {
    index: "src/index.ts",
    "algorand/index": "src/algorand/index.ts",
    "solana/index": "src/solana/index.ts",
    "dex/index": "src/dex/index.ts",
    "api/index": "src/api/index.ts",
    "client/index": "src/client/index.ts",
    "orchestration/index": "src/orchestration/index.ts",
    "workflow/index": "src/workflow/index.ts",
  },
  format: ["esm", "cjs"],
  dts: true,
  splitting: true,
  sourcemap: true,
  clean: true,
  treeshake: true,
  external: ["algosdk", "@solana/web3.js", "@solana/spl-token", "@noble/curves", "@tinymanorg/tinyman-js-sdk"],
});
