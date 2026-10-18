import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { contracts, require, serveUntilParentCloses, startChain } from './hardhat-chain.mjs';

// Loopback-only pinned Uniswap V3 + AzoaDexExecutor chain for the .NET DEX protocol fixture; see AGENTS.md.
const solc = require('solc');
const { ContractFactory, NonceManager, ZeroAddress, keccak256, toUtf8Bytes } = require('ethers');
const json = relative => JSON.parse(fs.readFileSync(path.join(contracts, relative), 'utf8'));

function token() {
  const input = { language: 'Solidity', sources: { 'DexTestToken.sol': { content: fs.readFileSync(path.join(contracts, 'test/DexTestToken.sol'), 'utf8') } },
    settings: { optimizer: { enabled: true, runs: 200 }, viaIR: true, evmVersion: 'shanghai', metadata: { bytecodeHash: 'none' },
      outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object'] } } } };
  const output = JSON.parse(solc.compile(JSON.stringify(input), { import: name => {
    const target = path.resolve(contracts, 'node_modules', name);
    if (!target.startsWith(path.join(contracts, 'node_modules') + path.sep)) return { error: 'Import escapes dependency directory.' };
    return { contents: fs.readFileSync(target, 'utf8') };
  } }));
  for (const error of output.errors ?? []) if (error.severity === 'error') throw new Error(error.formattedMessage);
  const compiled = output.contracts['DexTestToken.sol'].DexTestToken;
  return { abi: compiled.abi, bytecode: '0x' + compiled.evm.bytecode.object };
}

const chain = await startChain({ totalAccounts: 5, balanceEther: 1000 });
const { url, provider, wallets } = chain;
const deployer = new NonceManager(wallets[0]);
const deploy = async (artifact, args = []) => {
  const contract = await new ContractFactory(artifact.abi, artifact.bytecode, deployer).deploy(...args);
  await contract.waitForDeployment();
  return contract;
};
// Digest is over the committed (LF) bytes; a Windows autocrlf checkout only changes line endings.
const wethBytes = Buffer.from(fs.readFileSync(path.join(contracts, 'test/fixtures/WETH9.json'), 'utf8').replace(/\r\n/g, '\n'), 'utf8');
if (createHash('sha256').update(wethBytes).digest('hex') !== '67f36a6b7174cc41af5e6e2bcf393d5da253c380c8de72becbe8c3173e34c8da')
  throw new Error('WETH9 fixture artifact digest differs from its pinned provenance.');
const weth = await deploy(JSON.parse(wethBytes.toString('utf8')));
const factory = await deploy(json('node_modules/@uniswap/v3-core/artifacts/contracts/UniswapV3Factory.sol/UniswapV3Factory.json'));
const router = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/SwapRouter.sol/SwapRouter.json'),
  [await factory.getAddress(), await weth.getAddress()]);
const quoter = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/lens/Quoter.sol/Quoter.json'),
  [await factory.getAddress(), await weth.getAddress()]);
const positions = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/NonfungiblePositionManager.sol/NonfungiblePositionManager.json'),
  [await factory.getAddress(), await weth.getAddress(), ZeroAddress]);
const artifact = token();
const input = await deploy(artifact); const output = await deploy(artifact);
for (const asset of [input, output]) {
  await (await asset.mint(wallets[0].address, 10n ** 24n)).wait();
  await (await asset.mint(wallets[1].address, 10n ** 22n)).wait();
}
const pair = [await input.getAddress(), await output.getAddress()].sort((a, b) => BigInt(a) < BigInt(b) ? -1 : 1);
await (await positions.createAndInitializePoolIfNecessary(pair[0], pair[1], 3000, 1n << 96n)).wait();
const liquidity = 20n * 10n ** 18n;
for (const asset of [input, output]) await (await asset.approve(await positions.getAddress(), liquidity)).wait();
await (await positions.mint({ token0: pair[0], token1: pair[1], fee: 3000, tickLower: -887220, tickUpper: 887220,
  amount0Desired: liquidity, amount1Desired: liquidity, amount0Min: 0, amount1Min: 0, recipient: wallets[0].address,
  deadline: 4102444800 })).wait();
const policyVersion = keccak256(toUtf8Bytes('local-dedicated-policy-v1'));
const executor = await deploy(json('artifacts/AzoaDexExecutor.json'),
  [await router.getAddress(), await factory.getAddress(), await weth.getAddress(), wallets[4].address, policyVersion]);
const codeHash = async contract => keccak256(await provider.getCode(await contract.getAddress()));
const genesis = await provider.send('eth_getBlockByNumber', ['0x0', false]);
// Ephemeral keys travel only over the private pipe; never written to disk.
serveUntilParentCloses(chain, {
  url, genesisHash: genesis.hash, policyVersion, keys: wallets.map(wallet => wallet.privateKey),
  executor: await executor.getAddress(), router: await router.getAddress(), factory: await factory.getAddress(),
  quoter: await quoter.getAddress(), weth: await weth.getAddress(), input: await input.getAddress(), output: await output.getAddress(),
  executorHash: await codeHash(executor), routerHash: await codeHash(router), factoryHash: await codeHash(factory),
  quoterHash: await codeHash(quoter), wethHash: await codeHash(weth), inputHash: await codeHash(input), outputHash: await codeHash(output),
});
