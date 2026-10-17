import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash, randomUUID } from 'node:crypto';
import { localChain } from '../scripts/hardhat-network.mjs';
import solc from 'solc';
import { AbiCoder, BrowserProvider, ContractFactory, NonceManager, Wallet, ZeroAddress, keccak256, toUtf8Bytes } from 'ethers';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const tuple = 'tuple(bytes32 operation,bytes32 quoteHash,address owner,address recipient,address tokenIn,address tokenOut,address pool,address treasury,uint256 grossInput,uint256 platformFee,uint256 minimumOutput,uint24 poolFee,uint256 deadline,uint256 feeRevision,bytes32 policyKeyVersion,bytes32 inputCodeHash,bytes32 outputCodeHash,bool nativeInput,bool nativeOutput)';
const policyVersion = keccak256(toUtf8Bytes('local-dedicated-policy-v1'));
const types = { Execution: [{ name: 'payloadHash', type: 'bytes32' }] };
const json = relative => JSON.parse(fs.readFileSync(path.join(root, relative), 'utf8'));

function tokenArtifact(name = 'DexTestToken') {
  const input = { language: 'Solidity', sources: { 'DexTestToken.sol': { content: fs.readFileSync(path.join(root, 'test/DexTestToken.sol'), 'utf8') } },
    settings: { optimizer: { enabled: true, runs: 200 }, viaIR: true, evmVersion: 'shanghai', metadata: { bytecodeHash: 'none' },
      outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object'] } } } };
  const output = JSON.parse(solc.compile(JSON.stringify(input), { import: name => {
    const target = path.resolve(root, 'node_modules', name);
    if (!target.startsWith(path.join(root, 'node_modules') + path.sep)) return { error: 'Import escapes dependency directory.' };
    return { contents: fs.readFileSync(target, 'utf8') };
  } }));
  for (const error of output.errors ?? []) if (error.severity === 'error') throw new Error(error.formattedMessage);
  const compiled = output.contracts['DexTestToken.sol'][name];
  return { abi: compiled.abi, bytecode: '0x' + compiled.evm.bytecode.object };
}

async function fixture() {
  const { provider: rpc, keys } = await localChain({ totalAccounts: 5, balanceEther: 1000 });
  const provider = new BrowserProvider(rpc);
  assert.equal((await provider.getNetwork()).chainId, 31337n);
  const wallets = keys.map(key => new Wallet(key, provider));
  const senders = wallets.map(wallet => new NonceManager(wallet));
  const deploy = async (artifact, args = []) => {
    const contract = await new ContractFactory(artifact.abi, artifact.bytecode, senders[0]).deploy(...args);
    await contract.waitForDeployment(); return contract;
  };
  const wethBytes = Buffer.from(fs.readFileSync(path.join(root, 'test/fixtures/WETH9.json'), 'utf8').replace(/\r\n/g, '\n'), 'utf8');
  assert.equal(createHash('sha256').update(wethBytes).digest('hex'), '67f36a6b7174cc41af5e6e2bcf393d5da253c380c8de72becbe8c3173e34c8da');
  const weth = await deploy(JSON.parse(wethBytes.toString('utf8')));
  const factory = await deploy(json('node_modules/@uniswap/v3-core/artifacts/contracts/UniswapV3Factory.sol/UniswapV3Factory.json'));
  const router = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/SwapRouter.sol/SwapRouter.json'), [await factory.getAddress(), await weth.getAddress()]);
  const quoter = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/lens/Quoter.sol/Quoter.json'), [await factory.getAddress(), await weth.getAddress()]);
  const positions = await deploy(json('node_modules/@uniswap/v3-periphery/artifacts/contracts/NonfungiblePositionManager.sol/NonfungiblePositionManager.json'),
    [await factory.getAddress(), await weth.getAddress(), ZeroAddress]);
  const token = tokenArtifact();
  const input = await deploy(token); const output = await deploy(token);
  for (const asset of [input, output]) {
    await (await asset.mint(wallets[0].address, 10n ** 24n)).wait();
    await (await asset.mint(wallets[1].address, 10n ** 22n)).wait();
  }
  await (await weth.deposit({ value: 100n * 10n ** 18n })).wait();
  const addPool = async (left, right) => {
    const addresses = [await left.getAddress(), await right.getAddress()].sort((a, b) => BigInt(a) < BigInt(b) ? -1 : 1);
    await (await positions.createAndInitializePoolIfNecessary(addresses[0], addresses[1], 3000, 1n << 96n)).wait();
    const liquidityAmount = 20n * 10n ** 18n;
    for (const asset of [left, right]) await (await asset.approve(await positions.getAddress(), liquidityAmount)).wait();
    await (await positions.mint({ token0: addresses[0], token1: addresses[1], fee: 3000, tickLower: -887220, tickUpper: 887220,
      amount0Desired: liquidityAmount, amount1Desired: liquidityAmount, amount0Min: 0, amount1Min: 0,
      recipient: wallets[0].address, deadline: 4102444800 })).wait();
    return factory.getPool(addresses[0], addresses[1], 3000);
  };
  const tokenPool = await addPool(input, output);
  const nativePool = await addPool(input, weth);
  const executor = await deploy(json('artifacts/AzoaDexExecutor.json'),
    [await router.getAddress(), await factory.getAddress(), await weth.getAddress(), wallets[4].address, policyVersion]);
  const domain = { name: 'AZOA DEX', version: '1', chainId: 31337, verifyingContract: await executor.getAddress() };
  const encode = e => AbiCoder.defaultAbiCoder().encode([tuple], [e]);
  const permit = async (e, signingDomain = domain, signer = wallets[4]) => {
    const payload = encode(e);
    return [payload, await signer.signTypedData(signingDomain, types, { payloadHash: keccak256(payload) })];
  };
  const effect = async (overrides = {}) => {
    const tokenIn = await input.getAddress(); const tokenOut = await output.getAddress();
    return { operation: keccak256(toUtf8Bytes('operation-' + randomUUID())), quoteHash: keccak256(toUtf8Bytes('persisted-fixture-quote')),
      owner: wallets[1].address, recipient: wallets[2].address, tokenIn, tokenOut, pool: tokenPool, treasury: wallets[3].address,
      grossInput: 10n ** 17n, platformFee: 10n ** 14n, minimumOutput: 1n, poolFee: 3000,
      deadline: 4102444800n, feeRevision: 1n, policyKeyVersion: policyVersion,
      inputCodeHash: keccak256(await provider.getCode(tokenIn)), outputCodeHash: keccak256(await provider.getCode(tokenOut)),
      nativeInput: false, nativeOutput: false, ...overrides };
  };
  const execute = async e => {
    const args = await permit(e);
    return (await executor.connect(senders[1]).execute(...args, { value: e.nativeInput ? e.grossInput : 0, gasLimit: 3_000_000 })).wait();
  };
  return { rpc, provider, wallets, senders, input, output, weth, factory, router, quoter, positions, executor, nativePool, domain, permit, effect, execute, deploy };
}

test('actual Uniswap pool settles exact input and atomic platform fee once', async () => {
  const f = await fixture();
  try {
    const e = await f.effect();
    const quoted = await f.quoter.quoteExactInputSingle.staticCall(e.tokenIn, e.tokenOut, e.poolFee, e.grossInput - e.platformFee, 0);
    e.minimumOutput = quoted;
    await (await f.input.connect(f.senders[1]).approve(await f.executor.getAddress(), e.grossInput)).wait();
    const ownerBefore = await f.input.balanceOf(e.owner);
    const treasuryBefore = await f.input.balanceOf(e.treasury);
    const outputBefore = await f.output.balanceOf(e.recipient);
    const receipt = await f.execute(e);
    assert.equal(ownerBefore - await f.input.balanceOf(e.owner), e.grossInput);
    assert.equal(await f.input.balanceOf(e.treasury) - treasuryBefore, e.platformFee);
    assert.equal(await f.output.balanceOf(e.recipient) - outputBefore, quoted);
    assert.equal(await f.input.allowance(await f.executor.getAddress(), await f.router.getAddress()), 0n);
    assert.equal(await f.executor.consumedOperations(e.operation), true);
    assert(receipt.logs.some(log => log.address.toLowerCase() === e.pool.toLowerCase()), 'actual pool emitted settlement evidence');
    await assert.rejects(f.execute(e));
  } finally { f.provider.destroy(); }
});

test('slippage failure rolls back owner debit, fee and operation consumption', async () => {
  const f = await fixture();
  try {
    const e = await f.effect({ minimumOutput: 10n ** 30n });
    await (await f.input.connect(f.senders[1]).approve(await f.executor.getAddress(), e.grossInput)).wait();
    const ownerBefore = await f.input.balanceOf(e.owner); const treasuryBefore = await f.input.balanceOf(e.treasury);
    await assert.rejects(f.execute(e));
    assert.equal(await f.input.balanceOf(e.owner), ownerBefore);
    assert.equal(await f.input.balanceOf(e.treasury), treasuryBefore);
    assert.equal(await f.executor.consumedOperations(e.operation), false);
  } finally { f.provider.destroy(); }
});

test('owner cannot forge zero fee, revision, treasury or another EIP712 domain', async () => {
  const f = await fixture();
  try {
    const e = await f.effect();
    await (await f.input.connect(f.senders[1]).approve(await f.executor.getAddress(), e.grossInput)).wait();
    const original = await f.permit(e);
    for (const changed of [{ platformFee: 0n }, { feeRevision: 2n }, { treasury: f.wallets[1].address }]) {
      const payload = (await f.permit({ ...e, ...changed }))[0];
      await assert.rejects(async () => (await f.executor.connect(f.senders[1]).execute(payload, original[1], { gasLimit: 3_000_000 })).wait());
    }
    for (const domain of [{ ...f.domain, chainId: 1 }, { ...f.domain, verifyingContract: f.wallets[3].address }]) {
      const args = await f.permit(e, domain);
      await assert.rejects(async () => (await f.executor.connect(f.senders[1]).execute(...args, { gasLimit: 3_000_000 })).wait());
    }
    const ownerPermit = await f.permit(e, f.domain, f.wallets[1]);
    await assert.rejects(async () => (await f.executor.connect(f.senders[1]).execute(...ownerPermit, { gasLimit: 3_000_000 })).wait());
  } finally { f.provider.destroy(); }
});

test('explicit native input and native output use the actual WETH pool', async () => {
  const f = await fixture();
  try {
    const weth = await f.weth.getAddress();
    const wethHash = keccak256(await f.provider.getCode(weth));
    const nativeInput = await f.effect({ tokenIn: weth, tokenOut: await f.input.getAddress(), pool: f.nativePool,
      inputCodeHash: wethHash, outputCodeHash: keccak256(await f.provider.getCode(await f.input.getAddress())), nativeInput: true });
    const treasuryBefore = BigInt(await f.provider.send('eth_getBalance', [nativeInput.treasury, 'latest']));
    await f.execute(nativeInput);
    assert.equal(BigInt(await f.provider.send('eth_getBalance', [nativeInput.treasury, 'latest'])) - treasuryBefore, nativeInput.platformFee);
    const nativeOutput = await f.effect({ tokenOut: weth, pool: f.nativePool, outputCodeHash: wethHash, nativeOutput: true, platformFee: 0n });
    await (await f.input.connect(f.senders[1]).approve(await f.executor.getAddress(), nativeOutput.grossInput)).wait();
    const before = BigInt(await f.provider.send('eth_getBalance', [nativeOutput.recipient, 'latest']));
    await f.execute(nativeOutput);
    assert(BigInt(await f.provider.send('eth_getBalance', [nativeOutput.recipient, 'latest'])) > before);
  } finally { f.provider.destroy(); }
});

test('executor preserves prior token dust and refuses changed owner or trailing ABI bytes', async () => {
  const f = await fixture();
  try {
    const address = await f.executor.getAddress();
    await (await f.input.mint(address, 71n)).wait();
    await (await f.output.mint(address, 83n)).wait();
    const e = await f.effect();
    await (await f.input.connect(f.senders[1]).approve(address, e.grossInput)).wait();
    const args = await f.permit(e);
    await assert.rejects(async () => (await f.executor.connect(f.senders[2]).execute(...args, { gasLimit: 3_000_000 })).wait());
    await assert.rejects(async () => (await f.executor.connect(f.senders[1]).execute(args[0] + '00', args[1], { gasLimit: 3_000_000 })).wait());
    assert.equal(await f.executor.consumedOperations(e.operation), false);
    await f.execute(e);
    assert.equal(await f.input.balanceOf(address), 71n);
    assert.equal(await f.output.balanceOf(address), 83n);
  } finally { f.provider.destroy(); }
});

test('native treasury callback cannot execute a second valid policy-authorized swap', async () => {
  const f = await fixture();
  try {
    const receiver = await f.deploy(tokenArtifact('DexNativeReceiver'));
    const receiverAddress = await receiver.getAddress();
    const weth = await f.weth.getAddress();
    const common = { owner: receiverAddress, treasury: receiverAddress, tokenIn: weth, tokenOut: await f.input.getAddress(),
      pool: f.nativePool, nativeInput: true, inputCodeHash: keccak256(await f.provider.getCode(weth)),
      outputCodeHash: keccak256(await f.provider.getCode(await f.input.getAddress())) };
    const first = await f.effect(common); const second = await f.effect(common);
    const encodeCall = async e => f.executor.interface.encodeFunctionData('execute', await f.permit(e));
    await (await receiver.configure(await f.executor.getAddress(), await encodeCall(second), second.grossInput, false)).wait();
    await (await receiver.run(await encodeCall(first), first.grossInput, { value: first.grossInput + second.grossInput, gasLimit: 4_000_000 })).wait();
    assert.equal(await receiver.attempted(), true);
    assert.equal(await receiver.succeeded(), false);
    assert.equal(await f.executor.consumedOperations(first.operation), true);
    assert.equal(await f.executor.consumedOperations(second.operation), false);
    await (await receiver.run(await encodeCall(second), second.grossInput, { gasLimit: 4_000_000 })).wait();
    assert.equal(await f.executor.consumedOperations(second.operation), true);
  } finally { f.provider.destroy(); }
});

test('refused native treasury payment rolls back the swap and fee', async () => {
  const f = await fixture();
  try {
    const receiver = await f.deploy(tokenArtifact('DexNativeReceiver'));
    await (await receiver.configure(await f.executor.getAddress(), '0x', 0, true)).wait();
    const weth = await f.weth.getAddress();
    const e = await f.effect({ tokenIn: weth, tokenOut: await f.input.getAddress(), pool: f.nativePool, nativeInput: true,
      treasury: await receiver.getAddress(), inputCodeHash: keccak256(await f.provider.getCode(weth)),
      outputCodeHash: keccak256(await f.provider.getCode(await f.input.getAddress())) });
    const outputBefore = await f.input.balanceOf(e.recipient);
    await assert.rejects(f.execute(e));
    assert.equal(await f.input.balanceOf(e.recipient), outputBefore);
    assert.equal(await f.executor.consumedOperations(e.operation), false);
    assert.equal(BigInt(await f.provider.send('eth_getBalance', [e.treasury, 'latest'])), 0n);
  } finally { f.provider.destroy(); }
});
