import { pins, sha256, upstreamAll } from './upstream.mjs';

// Verifies every pinned upstream file is obtainable at the pinned commit and prints its sha256; see ../AGENTS.md §Pins.
const files = await upstreamAll();
for (const [relative, bytes] of Object.entries(files)) console.log(`${sha256(bytes)}  ${relative}`);
console.log(`verified ${Object.keys(files).length} files at ${pins.upstream.commit}`);
