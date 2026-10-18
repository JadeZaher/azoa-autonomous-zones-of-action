import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

// Pinned Wormhole source fetch: every file is verified against its git blob id from the pinned tree; see ../AGENTS.md §Pins.
export const fixtureRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const pins = JSON.parse(fs.readFileSync(path.join(fixtureRoot, 'wormhole-pins.json'), 'utf8'));
export const cacheRoot = path.join(fixtureRoot, '.cache');
export const stateRoot = path.join(fixtureRoot, '.state');

/** Git blob object id (`sha1("blob <length>\0" + bytes)`), the identity the pinned tree records. */
export function gitBlobId(bytes) {
  return createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
}

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

/** Returns the bytes of one pinned upstream path, fetching once into .cache and failing closed on any id mismatch. */
export async function upstreamFile(relative) {
  const expected = pins.upstream.gitBlobs[relative];
  if (!expected) throw new Error(`Upstream path is not pinned: ${relative}`);
  const target = path.join(cacheRoot, 'upstream', ...relative.split('/'));
  if (fs.existsSync(target)) {
    const cached = fs.readFileSync(target);
    if (gitBlobId(cached) === expected) return cached;
    fs.rmSync(target);
  }
  const url = `https://raw.githubusercontent.com/${pins.upstream.repository}/${pins.upstream.commit}/${relative}`;
  const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
  if (!response.ok) throw new Error(`Pinned upstream fetch failed (${response.status}) for ${relative}.`);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (gitBlobId(bytes) !== expected) throw new Error(`Upstream ${relative} differs from pinned blob ${expected}.`);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, bytes);
  return bytes;
}

/** Fetches and verifies every pinned upstream path. */
export async function upstreamAll() {
  const files = {};
  for (const relative of Object.keys(pins.upstream.gitBlobs)) files[relative] = await upstreamFile(relative);
  return files;
}

/** Writes one JSON state document for the C# protocol tests. */
export function writeState(name, value) {
  fs.mkdirSync(stateRoot, { recursive: true });
  fs.writeFileSync(path.join(stateRoot, name), JSON.stringify(value, null, 2) + '\n');
}

export const readState = name => JSON.parse(fs.readFileSync(path.join(stateRoot, name), 'utf8'));

/** Numeric-loopback RPC only: fixtures never fund or sign against a named or public host. */
export function requireLoopback(url) {
  const host = new URL(url).hostname;
  if (host !== '127.0.0.1' && host !== '[::1]') throw new Error(`Fixture RPC must be numeric loopback, got ${host}.`);
  return url;
}
