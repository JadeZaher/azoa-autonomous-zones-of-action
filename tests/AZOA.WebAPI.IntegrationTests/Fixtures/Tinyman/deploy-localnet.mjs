import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Creates the pinned Tinyman v2 validator on the isolated AlgoKit LocalNet (Fixtures/Algorand) and re-verifies its bytes; see AGENTS.md.
const here = path.dirname(fileURLToPath(import.meta.url));
const pin = JSON.parse(fs.readFileSync(path.join(here, 'validator-pin.json'), 'utf8'));
const algod = new URL(process.env.AZOA_ALGORAND_LOCAL_ALGOD ?? 'http://127.0.0.1:4001/');
const token = process.env.AZOA_ALGORAND_LOCAL_TOKEN ?? 'a'.repeat(64);
const runtime = process.env.AZOA_CONTAINER_RUNTIME ?? 'podman';
const container = 'azoa_native_avm_algod';
if (!['127.0.0.1', 'localhost', '[::1]'].includes(algod.hostname)) throw new Error('Tinyman fixture deploys only to a loopback LocalNet.');

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const approval = Buffer.from(pin.approvalProgramBase64, 'base64');
const clear = Buffer.from(pin.clearStateProgramBase64, 'base64');
if (sha256(approval) !== pin.approvalSha256 || sha256(clear) !== pin.clearStateSha256)
  throw new Error('validator-pin.json program bytes differ from their recorded digests.');

async function algodJson(relative) {
  const response = await fetch(new URL(relative, algod), { headers: { 'X-Algo-API-Token': token } });
  if (!response.ok) throw new Error(`algod ${relative} -> ${response.status}`);
  return response.json();
}
const goal = args => execFileSync(runtime, ['exec', container, 'goal', ...args], { encoding: 'utf8' });

const params = await algodJson('v2/transactions/params');
const mount = path.resolve(here, '../Algorand/goal_mount');
fs.mkdirSync(mount, { recursive: true });
fs.writeFileSync(path.join(mount, 'tinyman-approval.bin'), approval);
fs.writeFileSync(path.join(mount, 'tinyman-clear.bin'), clear);
const creator = process.env.AZOA_TINYMAN_LOCAL_CREATOR
  ?? goal(['account', 'list']).split('\n').map(line => line.split(/\s+/).find(word => /^[A-Z2-7]{58}$/.test(word))).find(Boolean);
if (!creator) throw new Error('No funded LocalNet account found in the default wallet.');
const created = goal(['app', 'create', '--creator', creator,
  '--approval-prog-raw', '/root/goal_mount/tinyman-approval.bin', '--clear-prog-raw', '/root/goal_mount/tinyman-clear.bin',
  '--global-byteslices', String(pin.globalStateSchema.byteSlices), '--global-ints', String(pin.globalStateSchema.uints),
  '--local-byteslices', String(pin.localStateSchema.byteSlices), '--local-ints', String(pin.localStateSchema.uints),
  '--extra-pages', String(pin.extraProgramPages)]);
const applicationId = Number(/app index (\d+)/i.exec(created)?.[1]);
if (!Number.isSafeInteger(applicationId) || applicationId <= 0) throw new Error('goal did not report a created application index.');

const deployed = await algodJson(`v2/applications/${applicationId}`);
if (sha256(Buffer.from(deployed.params['approval-program'], 'base64')) !== pin.approvalSha256 ||
    sha256(Buffer.from(deployed.params['clear-state-program'], 'base64')) !== pin.clearStateSha256)
  throw new Error('LocalNet validator bytes differ from the reviewed mainnet/testnet pin.');

// Configuration the protocol fixture feeds to AlgorandProvider (Dex:Tinyman:Networks:Devnet:*).
process.stdout.write(JSON.stringify({
  ValidatorAppId: String(applicationId), ApprovalSha256: pin.approvalSha256, ClearStateSha256: pin.clearStateSha256,
  GenesisHash: params['genesis-hash'], CreatedRound: deployed.params['created-at-round'] ?? null,
}) + '\n');
