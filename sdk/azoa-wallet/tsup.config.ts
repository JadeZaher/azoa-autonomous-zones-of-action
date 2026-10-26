import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { defineConfig } from "tsup";
import type { Options } from "tsup";

type TsupPlugin = NonNullable<Options["plugins"]>[number];

/** Built files that must start with the "use client" directive (the React entry only). */
const CLIENT_ENTRY_FILES = /(^|[\\/])react[\\/]index\.c?js$/;

/** Prepends "use client" to the React entry after tree-shaking; see `src/react/AGENTS.md` §build. */
const useClientDirective: TsupPlugin = {
  name: "use-client-directive",
  async buildEnd({ writtenFiles }) {
    for (const file of writtenFiles) {
      if (!CLIENT_ENTRY_FILES.test(file.name)) continue;
      const path = resolve(file.name);
      const code = await readFile(path, "utf8");
      if (code.startsWith('"use client";')) continue;
      await writeFile(path, `"use client";\n${code}`);
      const mapPath = `${path}.map`;
      const map = JSON.parse(await readFile(mapPath, "utf8")) as { mappings: string };
      map.mappings = `;${map.mappings}`;
      await writeFile(mapPath, JSON.stringify(map));
    }
  },
};

export default defineConfig({
  entry: {
    index: "src/index.ts",
    "algorand/index": "src/algorand/index.ts",
    "solana/index": "src/solana/index.ts",
    "api/index": "src/api/index.ts",
    "client/index": "src/client/index.ts",
    "workflow/index": "src/workflow/index.ts",
    "money/index": "src/money/index.ts",
    "money/testing/index": "src/money/testing/index.ts",
    "react/index": "src/react/index.ts",
  },
  format: ["esm", "cjs"],
  dts: true,
  splitting: true,
  sourcemap: true,
  clean: true,
  treeshake: true,
  external: ["algosdk", "@solana/web3.js", "@solana/spl-token", "@noble/curves", "react", "react/jsx-runtime"],
  plugins: [useClientDirective],
});
