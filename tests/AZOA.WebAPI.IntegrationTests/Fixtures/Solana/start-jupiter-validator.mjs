import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

// Starts the local Agave validator with Jupiter v6 + Orca Whirlpool cloned from mainnet and verifies the pinned program bytes; see AGENTS.md §Jupiter.
const here = path.dirname(fileURLToPath(import.meta.url));
const pin = JSON.parse(fs.readFileSync(path.join(here, 'jupiter-whirlpool-pin.json'), 'utf8'));
const require = createRequire(path.resolve(here, '../../../../contracts/ethereum/package.json'));
const { ed25519 } = require('@noble/curves/ed25519');
const runtime = process.env.AZOA_CONTAINER_RUNTIME ?? 'podman';
const container = process.env.AZOA_SOLANA_JUPITER_CONTAINER ?? 'azoa-solana-jupiter';
const image = process.env.AZOA_SOLANA_VALIDATOR_IMAGE ?? 'localhost/azoa-solana-validator:agave3.1.8';
const port = Number(process.env.AZOA_SOLANA_JUPITER_PORT ?? 18999);
const local = `http://127.0.0.1:${port}`;

const alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
const encode = bytes => {
  let value = BigInt('0x' + (Buffer.from(bytes).toString('hex') || '0')), text = '';
  while (value > 0n) { text = alphabet[Number(value % 58n)] + text; value /= 58n; }
  for (const byte of bytes) { if (byte) break; text = '1' + text; }
  return text;
};
const decode = text => {
  let value = 0n;
  for (const character of text) value = value * 58n + BigInt(alphabet.indexOf(character));
  const hex = value.toString(16).padStart(64, '0');
  return Buffer.from(hex, 'hex');
};
const onCurve = bytes => { try { ed25519.ExtendedPoint.fromHex(bytes); return true; } catch { return false; } };
// Solana find_program_address: highest bump whose SHA-256 digest is off the ed25519 curve.
const programAddress = (seeds, program) => {
  for (let bump = 255; bump >= 0; bump--) {
    const digest = createHash('sha256').update(Buffer.concat([...seeds, Buffer.from([bump]), decode(program), Buffer.from('ProgramDerivedAddress')])).digest();
    if (!onCurve(digest)) return encode(digest);
  }
  throw new Error('No program address.');
};
const rpc = async (url, method, params) => {
  const response = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) });
  const body = await response.json();
  if (body.error) throw new Error(`${method}: ${body.error.message}`);
  return body.result;
};
const account = async (url, key) => {
  const result = await rpc(url, 'getAccountInfo', [key, { encoding: 'base64', commitment: 'confirmed' }]);
  return result.value && { owner: result.value.owner, data: Buffer.from(result.value.data[0], 'base64') };
};

// Tick arrays are derived from the pool's tick at clone time, since mainnet price moves after the pin.
const whirlpool = pin.programs.find(program => program.programId.startsWith('whirL')).programId;
const pool = await account(pin.source, pin.pool.address);
if (!pool || pool.owner !== whirlpool || pool.data.length !== pin.pool.dataLength) throw new Error('Pinned Whirlpool pool changed owner or layout.');
const span = 88 * pool.data.readUInt16LE(41);
const start = Math.floor(pool.data.readInt32LE(81) / span) * span;
const tickArrays = [-2, -1, 0, 1, 2].map(step =>
  programAddress([Buffer.from('tick_array'), decode(pin.pool.address), Buffer.from(String(start + step * span))], whirlpool));
const clones = [pin.pool.address, pin.pool.whirlpoolsConfig, pin.pool.tokenMintA, pin.pool.tokenVaultA, pin.pool.tokenMintB, pin.pool.tokenVaultB, ...tickArrays];

const args = ['run', '-d', '--name', container, '--publish', `127.0.0.1:${port}:8899`, image,
  '--ledger', '/ledger/state', '--rpc-port', '8899', '--bind-address', '0.0.0.0', '--url', pin.source,
  ...pin.programs.flatMap(program => ['--clone-upgradeable-program', program.programId]),
  ...clones.flatMap(key => ['--clone', key]), '--maybe-clone', pin.pool.oracle];
if (process.argv.includes('--dry-run')) process.stdout.write(JSON.stringify({ runtime, args }, null, 2) + '\n');
else await launch();

async function launch() {
  execFileSync(runtime, args, { stdio: ['ignore', 'ignore', 'inherit'] });
  for (let attempt = 0; ; attempt++) {
    try { if ((await rpc(local, 'getHealth', [])) === 'ok') break; } catch { /* starting */ }
    if (attempt > 120) throw new Error('Local validator did not become healthy.');
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  for (const program of pin.programs) {
    const data = await account(local, program.programData);
    const digest = data && createHash('sha256').update(data.data.subarray(45)).digest('hex');
    if (digest !== program.executableSha256)
      throw new Error(`${program.programId} was upgraded on mainnet since the pin (${program.lastDeploySlot}); review before re-pinning.`);
  }
  process.stdout.write(JSON.stringify({ url: local, container, pool: pin.pool.address, tickArrays, oracle: pin.pool.oracle }) + '\n');
}
