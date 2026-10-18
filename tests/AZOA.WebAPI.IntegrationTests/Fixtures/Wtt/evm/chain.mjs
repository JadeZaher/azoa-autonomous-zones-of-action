import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { cacheRoot, fixtureRoot, pins, readState, requireLoopback, upstreamAll, writeState } from '../lib/upstream.mjs';
import { restoreGuardianSet } from '../lib/guardian.mjs';

// Deploys the pinned Wormhole core + token bridge on a loopback Hardhat node and registers foreign emitters
// with locally signed governance VAAs. Usage: `compile` | `deploy --rpc <url>` | `register --vaa <hex>` (from ../lib/vaa.mjs register-chain).
// See ../AGENTS.md §EVM.
const require = createRequire(path.join(fixtureRoot, 'package.json'));
const solc = require('solc');
const { ContractFactory, Contract, JsonRpcProvider, NonceManager, Wallet } = require('ethers');
const repository = path.resolve(fixtureRoot, '../../../..');
const hardhatDeployerKey = '0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80'; // Hardhat account #0 (public test key).
const entries = ['Implementation.sol', 'Setup.sol', 'Wormhole.sol', 'bridge/BridgeImplementation.sol', 'bridge/BridgeSetup.sol',
  'bridge/TokenBridge.sol', 'bridge/token/TokenImplementation.sol'];

function argument(name) {
  const index = process.argv.indexOf(name);
  return index > 0 ? process.argv[index + 1] : undefined;
}

async function compile() {
  if (solc.version().split('+')[0] !== pins.evm.solc) throw new Error(`solc ${solc.version()} is not the pinned ${pins.evm.solc}.`);
  const ozPackage = JSON.parse(fs.readFileSync(path.join(fixtureRoot, 'node_modules/@openzeppelin/contracts/package.json'), 'utf8'));
  if (ozPackage.version !== pins.evm.openzeppelin.version) throw new Error(`OpenZeppelin ${ozPackage.version} is not the pinned version.`);
  await upstreamAll();
  const contractsRoot = path.join(cacheRoot, 'upstream/ethereum/contracts');
  const sources = Object.fromEntries(entries.map(entry => [`contracts/${entry}`, { content: fs.readFileSync(path.join(contractsRoot, entry), 'utf8') }]));
  const input = { language: 'Solidity', sources, settings: { optimizer: { enabled: true, runs: pins.evm.optimizerRuns },
    evmVersion: pins.evm.evmVersion, outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object', 'evm.deployedBytecode.object'] } } } };
  const output = JSON.parse(solc.compile(JSON.stringify(input), { import: name => {
    const target = name.startsWith('@openzeppelin/')
      ? path.resolve(fixtureRoot, 'node_modules', name) : path.resolve(cacheRoot, 'upstream/ethereum', name);
    const allowed = [path.join(fixtureRoot, 'node_modules', '@openzeppelin') + path.sep, contractsRoot + path.sep];
    if (!allowed.some(prefix => target.startsWith(prefix))) return { error: `Import escapes pinned sources: ${name}` };
    return fs.existsSync(target) ? { contents: fs.readFileSync(target, 'utf8') } : { error: `Unpinned import: ${name}` };
  } }));
  for (const error of output.errors ?? []) if (error.severity === 'error') throw new Error(error.formattedMessage);
  const artifact = (file, name) => {
    const compiled = output.contracts[`contracts/${file}`][name];
    return { abi: compiled.abi, bytecode: '0x' + compiled.evm.bytecode.object };
  };
  return {
    implementation: artifact('Implementation.sol', 'Implementation'), setup: artifact('Setup.sol', 'Setup'),
    wormhole: artifact('Wormhole.sol', 'Wormhole'), bridgeImplementation: artifact('bridge/BridgeImplementation.sol', 'BridgeImplementation'),
    bridgeSetup: artifact('bridge/BridgeSetup.sol', 'BridgeSetup'), tokenBridge: artifact('bridge/TokenBridge.sol', 'TokenBridge'),
    tokenImplementation: artifact('bridge/token/TokenImplementation.sol', 'TokenImplementation'),
  };
}

async function deploy(rpc) {
  const provider = new JsonRpcProvider(requireLoopback(rpc), pins.evm.chainId, { staticNetwork: true });
  if (Number((await provider.getNetwork()).chainId) !== pins.evm.chainId) throw new Error('Unexpected EVM chain id.');
  const deployer = new NonceManager(new Wallet(hardhatDeployerKey, provider));
  const guardians = restoreGuardianSet(readState('guardians.json'));
  const artifacts = await compile();
  const create = async (artifact, args = []) => {
    const contract = await new ContractFactory(artifact.abi, artifact.bytecode, deployer).deploy(...args);
    await contract.waitForDeployment();
    return contract;
  };
  const governance = '0x' + pins.guardian.governanceEmitter;
  const implementation = await create(artifacts.implementation);
  const setup = await create(artifacts.setup);
  const coreInit = setup.interface.encodeFunctionData('setup', [await implementation.getAddress(), guardians.addresses,
    pins.evm.wormholeChainId, pins.guardian.governanceChainId, governance, pins.evm.chainId]);
  const core = await create(artifacts.wormhole, [await setup.getAddress(), coreInit]);
  const wethBytes = fs.readFileSync(path.join(repository, pins.evm.weth9.artifact));
  if (createHash('sha256').update(wethBytes).digest('hex') !== pins.evm.weth9.sha256) throw new Error('WETH9 artifact differs from its pin.');
  const weth = await create(JSON.parse(wethBytes.toString('utf8')));
  const tokenImplementation = await create(artifacts.tokenImplementation);
  const bridgeImplementation = await create(artifacts.bridgeImplementation);
  const bridgeSetup = await create(artifacts.bridgeSetup);
  const bridgeInit = bridgeSetup.interface.encodeFunctionData('setup', [await bridgeImplementation.getAddress(), pins.evm.wormholeChainId,
    await core.getAddress(), pins.guardian.governanceChainId, governance, await tokenImplementation.getAddress(),
    await weth.getAddress(), 1, pins.evm.chainId]);
  const tokenBridge = await create(artifacts.tokenBridge, [await bridgeSetup.getAddress(), bridgeInit]);
  const codeHash = async address => createHash('sha256').update(Buffer.from((await provider.getCode(address)).slice(2), 'hex')).digest('hex');
  const genesis = await provider.getBlock(0);
  writeState('evm.json', { rpc, chainId: pins.evm.chainId, wormholeChainId: pins.evm.wormholeChainId, genesisHash: genesis.hash,
    core: await core.getAddress(), coreImplementation: await implementation.getAddress(),
    coreImplementationCodeSha256: await codeHash(await implementation.getAddress()),
    tokenBridge: await tokenBridge.getAddress(), tokenBridgeEmitterHex: (await tokenBridge.getAddress()).slice(2).toLowerCase().padStart(64, '0'),
    tokenBridgeImplementation: await bridgeImplementation.getAddress(),
    tokenBridgeImplementationCodeSha256: await codeHash(await bridgeImplementation.getAddress()),
    weth: await weth.getAddress(), registered: {} });
  console.log(JSON.stringify(readState('evm.json')));
}

async function register(vaaHex) {
  const state = readState('evm.json');
  const provider = new JsonRpcProvider(requireLoopback(state.rpc), state.chainId, { staticNetwork: true });
  const deployer = new NonceManager(new Wallet(hardhatDeployerKey, provider));
  const vaa = Buffer.from(vaaHex, 'hex');
  const payload = vaa.subarray(6 + vaa[5] * 66 + 51);
  const chain = payload.readUInt16BE(35);
  const emitter = '0x' + payload.subarray(37, 69).toString('hex');
  const bridge = new Contract(state.tokenBridge, ['function registerChain(bytes)', 'function bridgeContracts(uint16) view returns (bytes32)'], deployer);
  await (await bridge.registerChain(vaa)).wait();
  const recorded = await bridge.bridgeContracts(chain);
  if (recorded !== emitter) throw new Error(`Chain ${chain} emitter registration did not land.`);
  writeState('evm.json', { ...state, registered: { ...state.registered, [chain]: recorded } });
}

const command = process.argv[2];
if (command === 'compile') console.log(Object.entries(await compile()).map(([name, artifact]) => `${name}:${(artifact.bytecode.length - 2) / 2}`).join(' '));
else if (command === 'deploy') await deploy(argument('--rpc'));
else if (command === 'register') await register(argument('--vaa'));
else throw new Error('Usage: chain.mjs compile | deploy --rpc <url> | register --vaa <hex>');
