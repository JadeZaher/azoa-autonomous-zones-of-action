import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import solc from 'solc';
import { keccak256 } from 'ethers';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const names = ['AzoaNft', 'AzoaDexExecutor'];
const input = {
  language: 'Solidity',
  sources: Object.fromEntries(names.map(name => [`${name}.sol`, { content: fs.readFileSync(path.join(root, `src/${name}.sol`), 'utf8') }])),
  settings: {
    optimizer: { enabled: true, runs: 200 }, viaIR: true, evmVersion: 'shanghai',
    metadata: { bytecodeHash: 'none' },
    outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object', 'evm.deployedBytecode.object', 'evm.deployedBytecode.immutableReferences'] } },
  },
};
const output = JSON.parse(solc.compile(JSON.stringify(input), {
  import: name => {
    const resolved = path.resolve(root, 'node_modules', name);
    if (!resolved.startsWith(path.join(root, 'node_modules') + path.sep)) return { error: 'Import escapes dependency directory.' };
    return { contents: fs.readFileSync(resolved, 'utf8') };
  },
}));
for (const error of output.errors ?? []) {
  if (error.severity === 'error') throw new Error(error.formattedMessage);
}
fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
for (const name of names) {
  const compiled = output.contracts[`${name}.sol`][name];
  const artifact = {
    compiler: solc.version(), sourceHash: keccak256(Buffer.from(input.sources[`${name}.sol`].content)),
    settings: input.settings, abi: compiled.abi, bytecode: '0x' + compiled.evm.bytecode.object,
    deployedBytecode: '0x' + compiled.evm.deployedBytecode.object,
    immutableReferences: compiled.evm.deployedBytecode.immutableReferences,
  };
  fs.writeFileSync(path.join(root, `artifacts/${name}.json`), JSON.stringify(artifact, null, 2) + '\n');
}
