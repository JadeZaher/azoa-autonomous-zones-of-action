import fs from 'node:fs';
import path from 'node:path';
import { cacheRoot, fixtureRoot, pins, sha256 } from '../lib/upstream.mjs';

// Read-only `solana program dump` equivalent: fetches the canonical Wormhole program bytes and fails closed unless they
// match the pinned digest; `--pin` records them instead. See ../AGENTS.md §Solana.
const programs = { core: 'worm2ZoG2kUd4vFXhvjh93UUH596ayRfgQ2MgjNMTth', tokenBridge: 'wormDTUJ6AWPNvk59vGQbDvGJmqbDTdgWgAqcLBCgUb' };
const metadataLength = 45; // UpgradeableLoaderState::ProgramData { slot: u64, upgrade_authority: Option<Pubkey> } + enum tag.
const rpc = process.env.WTT_SOLANA_SOURCE_RPC ?? 'https://api.mainnet-beta.solana.com';
const pinMode = process.argv.includes('--pin');

async function call(method, params) {
  const response = await fetch(rpc, { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }), signal: AbortSignal.timeout(60_000) });
  const body = await response.json();
  if (!response.ok || body.error) throw new Error(`${method} failed: ${JSON.stringify(body.error ?? response.status)}`);
  return body.result.value;
}

const recorded = {};
for (const [role, programId] of Object.entries(programs)) {
  const program = await call('getAccountInfo', [programId, { encoding: 'jsonParsed' }]);
  const programData = program?.data?.parsed?.info?.programData;
  if (program?.owner !== 'BPFLoaderUpgradeab1e11111111111111111111111' || !programData)
    throw new Error(`${role} ${programId} is not an upgradeable program.`);
  const account = await call('getAccountInfo', [programData, { encoding: 'base64' }]);
  const raw = Buffer.from(account.data[0], 'base64');
  const deploymentSlot = raw.readBigUInt64LE(4).toString();
  const bytes = raw.subarray(metadataLength);
  const entry = { programId, programData, deploymentSlot, length: bytes.length, sha256: sha256(bytes) };
  const pinned = pins.solana.programs[role];
  if (!pinMode && (pinned?.sha256 !== entry.sha256 || pinned?.programId !== programId))
    throw new Error(`${role} program bytes differ from the pin (slot ${deploymentSlot}); re-pin only after review.`);
  fs.mkdirSync(path.join(cacheRoot, 'solana'), { recursive: true });
  fs.writeFileSync(path.join(cacheRoot, 'solana', `${role}.so`), bytes);
  recorded[role] = entry;
}
if (pinMode) {
  const file = path.join(fixtureRoot, 'wormhole-pins.json');
  const next = JSON.parse(fs.readFileSync(file, 'utf8'));
  next.solana.programs = recorded;
  fs.writeFileSync(file, JSON.stringify(next, null, 2) + '\n');
}
console.log(JSON.stringify(recorded));
