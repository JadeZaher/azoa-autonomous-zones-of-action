import fs from 'node:fs';
import path from 'node:path';
import { readState, stateRoot, writeState } from './upstream.mjs';
import { createGuardianSet, governanceVaa, guardianSetUpgradePayload, guardianState, registerChainPayload,
  restoreGuardianSet } from './guardian.mjs';

// Local guardian CLI for the runner: `guardians [--size n]` | `guardian-set-upgrade` | `register-chain --chain <id> --emitter <hex32>`.
// Prints VAA hex; governance sequences are tracked in .state so no two governance VAAs collide. See ../AGENTS.md §Guardian.
const argument = name => process.argv[process.argv.indexOf(name) + 1];
const nextSequence = () => {
  const file = path.join(stateRoot, 'governance.json');
  const sequence = (fs.existsSync(file) ? readState('governance.json').sequence : 1000) + 1;
  writeState('governance.json', { sequence });
  return sequence;
};
const command = process.argv[2];
if (command === 'guardians') {
  writeState('guardians.json', guardianState(createGuardianSet(Number(process.argv.includes('--size') ? argument('--size') : 1))));
  console.log(readState('guardians.json').addresses.join(','));
} else {
  const guardians = restoreGuardianSet(readState('guardians.json'));
  if (command === 'guardian-set-upgrade')
    console.log(governanceVaa(guardians, guardianSetUpgradePayload(guardians.index, guardians.addresses), nextSequence()).toString('hex'));
  else if (command === 'register-chain')
    console.log(governanceVaa(guardians, registerChainPayload(Number(argument('--chain')), argument('--emitter')), nextSequence()).toString('hex'));
  else throw new Error('Usage: vaa.mjs guardians [--size n] | guardian-set-upgrade | register-chain --chain <id> --emitter <hex32>');
}
