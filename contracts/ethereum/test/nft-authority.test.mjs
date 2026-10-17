import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import ganache from 'ganache';
import solc from 'solc';
import { AbiCoder, BrowserProvider, ContractFactory, NonceManager, Wallet, ZeroAddress, id, keccak256 } from 'ethers';

const artifact = JSON.parse(fs.readFileSync(new URL('../artifacts/AzoaNft.json', import.meta.url), 'utf8'));
const tuple = 'tuple(bytes32 lifecycle,uint8 operation,uint8 stage,uint32 generation,uint256 tokenId,address from,address to,address actor,address issuer,address beneficiary,bool soulbound,bool finalEffect,string metadata,uint256 fee,uint256 quotedFee,address treasury,uint256 revision,address payer,uint256 deadline)';
const receiverBuild = JSON.parse(solc.compile(JSON.stringify({ language: 'Solidity',
  sources: { 'AdversarialReceiver.sol': { content: fs.readFileSync(new URL('./AdversarialReceiver.sol', import.meta.url), 'utf8') } },
  settings: { evmVersion: 'shanghai', outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object'] } } },
})));
if (receiverBuild.errors?.some(error => error.severity === 'error')) throw new Error('Adversarial receiver fixture did not compile.');
const receiverArtifact = receiverBuild.contracts['AdversarialReceiver.sol'].AdversarialReceiver;

async function fixture() {
  const rpc = ganache.provider({ logging: { quiet: true }, chain: { chainId: 31337, hardfork: 'shanghai' }, wallet: { totalAccounts: 5 } });
  const provider = new BrowserProvider(rpc);
  const wallets = Object.values(rpc.getInitialAccounts()).map(account => new Wallet(account.secretKey, provider));
  const senders = wallets.map(wallet => new NonceManager(wallet));
  const contract = await new ContractFactory(artifact.abi, artifact.bytecode, senders[0]).deploy(wallets[0].address);
  await contract.waitForDeployment();
  const domain = { name: 'AZOA NFT', version: '1', chainId: 31337, verifyingContract: await contract.getAddress() };
  const types = { Execution: [{ name: 'payloadHash', type: 'bytes32' }] };
  const encode = effect => AbiCoder.defaultAbiCoder().encode([tuple], [effect]);
  const effect = (overrides = {}) => ({ lifecycle: id('logical-mint'), operation: 0, stage: 0, generation: 0,
    tokenId: 101n, from: ZeroAddress, to: wallets[1].address, actor: wallets[1].address,
    issuer: ZeroAddress, beneficiary: wallets[1].address, soulbound: false, finalEffect: true,
    metadata: 'ipfs://immutable-nft', fee: 100n, quotedFee: 100n, treasury: wallets[3].address,
    revision: 1n, payer: wallets[1].address, deadline: 4102444800n, ...overrides });
  async function permits(e, signer = 1, signingDomain = domain) {
    const payload = encode(e);
    const value = { payloadHash: keccak256(payload) };
    return [payload, await wallets[signer].signTypedData(signingDomain, types, value), await wallets[0].signTypedData(signingDomain, types, value)];
  }
  async function execute(e, signer = 1, payer = 1) {
    const args = await permits(e, signer);
    return (await contract.connect(senders[payer]).execute(...args, { value: e.fee, gasLimit: 2_000_000 })).wait();
  }
  return { rpc, provider, wallets, senders, contract, domain, encode, effect, permits, execute };
}

test('mint and fee are atomic, replay cannot charge twice, standard mutations cannot bypass execution', async () => {
  const f = await fixture();
  try {
    const e = f.effect();
    const before = BigInt(await f.provider.send('eth_getBalance', [f.wallets[3].address, 'latest']));
    await f.execute(e);
    assert.equal(await f.contract.ownerOf(e.tokenId), f.wallets[1].address);
    const after = BigInt(await f.provider.send('eth_getBalance', [f.wallets[3].address, 'latest']));
    assert.equal(after - before, 100n);
    await assert.rejects(f.execute(e));
    await assert.rejects(f.contract.connect(f.senders[1]).transferFrom(f.wallets[1].address, f.wallets[2].address, e.tokenId));
    await assert.rejects(f.contract.connect(f.senders[1])['safeTransferFrom(address,address,uint256)'](f.wallets[1].address, f.wallets[2].address, e.tokenId));
    await assert.rejects(f.contract.connect(f.senders[1]).approve(f.wallets[2].address, e.tokenId));
    await assert.rejects(f.contract.connect(f.senders[1]).setApprovalForAll(f.wallets[2].address, true));
  } finally { await f.rpc.disconnect(); }
});

test('owner cannot forge zero policy, treasury, revision, chain or contract domain', async () => {
  const f = await fixture();
  try {
    const e = f.effect();
    const original = await f.permits(e);
    for (const altered of [f.effect({ fee: 0n, quotedFee: 0n }), f.effect({ revision: 2n }), f.effect({ treasury: f.wallets[1].address })]) {
      const own = await f.permits(altered);
      await assert.rejects((async () => (await f.contract.connect(f.senders[1]).execute(own[0], own[1], original[2], { value: altered.fee, gasLimit: 2_000_000 })).wait())());
    }
    for (const domain of [{ ...f.domain, chainId: 1 }, { ...f.domain, verifyingContract: f.wallets[4].address }]) {
      const args = await f.permits(e, 1, domain);
      await assert.rejects((async () => (await f.contract.connect(f.senders[1]).execute(...args, { value: e.fee, gasLimit: 2_000_000 })).wait())());
    }
    assert.equal(await f.contract.everMinted(e.tokenId), false);
  } finally { await f.rpc.disconnect(); }
});

test('credential delivery requires exact recipient acceptance and preserves immutable issuer', async () => {
  const f = await fixture();
  try {
    const common = { soulbound: true, issuer: f.wallets[1].address, beneficiary: f.wallets[2].address };
    await f.execute(f.effect({ ...common, to: f.wallets[0].address, fee: 0n, finalEffect: false }));
    const deliver = f.effect({ ...common, stage: 2, from: f.wallets[0].address, to: f.wallets[2].address });
    await assert.rejects(f.execute(deliver));
    const accept = f.effect({ ...common, stage: 1, from: f.wallets[0].address, to: f.wallets[2].address,
      fee: 0n, finalEffect: false, payer: f.wallets[2].address });
    await f.execute(accept, 2, 2);
    await assert.rejects(f.execute({ ...deliver, to: f.wallets[4].address, beneficiary: f.wallets[4].address }));
    await assert.rejects(f.execute({ ...deliver, fee: 0n, quotedFee: 0n }));
    await f.execute({ ...deliver, generation: 1 });
    assert.equal(await f.contract.ownerOf(101n), f.wallets[2].address);
    assert.equal(await f.contract.issuerOf(101n), f.wallets[1].address);
    await assert.rejects(f.execute(f.effect({ ...common, lifecycle: id('forged-transfer'), operation: 1, stage: 3,
      from: f.wallets[2].address, to: f.wallets[4].address, actor: f.wallets[2].address, payer: f.wallets[2].address }), 2, 2));
    await f.execute(f.effect({ ...common, lifecycle: id('issuer-revoke'), operation: 3, stage: 6,
      from: f.wallets[2].address, to: ZeroAddress }));
    await assert.rejects(f.contract.ownerOf(101n));
    assert.equal(await f.contract.issuerOf(101n), f.wallets[1].address);
  } finally { await f.rpc.disconnect(); }
});

test('burned token identity cannot be reminted under a new lifecycle or issuer', async () => {
  const f = await fixture();
  try {
    await f.execute(f.effect());
    await f.execute(f.effect({ lifecycle: id('burn'), operation: 2, stage: 5, from: f.wallets[1].address, to: ZeroAddress }));
    await assert.rejects(f.execute(f.effect({ lifecycle: id('remint') })));
    assert.equal(await f.contract.everMinted(101n), true);
  } finally { await f.rpc.disconnect(); }
});

test('treasury callback cannot reenter and a rejecting receiver rolls back NFT and fee', async () => {
  const f = await fixture();
  try {
    const receiver = await new ContractFactory(receiverArtifact.abi, '0x' + receiverArtifact.evm.bytecode.object, f.senders[0]).deploy();
    await receiver.waitForDeployment();
    const recipient = await receiver.getAddress();
    const e = f.effect({ treasury: recipient });
    const args = await f.permits(f.effect({ lifecycle: id('valid-reentrant-effect'), tokenId: 104n,
      fee: 0n, quotedFee: 0n, payer: recipient }));
    await (await receiver.configure(await f.contract.getAddress(), f.contract.interface.encodeFunctionData('execute', args), false)).wait();
    await f.execute(e);
    assert.equal(await receiver.blocked(), true);
    assert.equal(await f.contract.everMinted(104n), false);
    assert.equal(BigInt(await f.provider.send('eth_getBalance', [recipient, 'latest'])), 100n);
    await (await receiver.configure(ZeroAddress, '0x', true)).wait();
    const rejected = f.effect({ lifecycle: id('rejected-receiver'), tokenId: 102n, to: recipient, beneficiary: recipient });
    const before = BigInt(await f.provider.send('eth_getBalance', [f.wallets[3].address, 'latest']));
    await assert.rejects(f.execute(rejected));
    assert.equal(await f.contract.everMinted(102n), false);
    assert.equal(BigInt(await f.provider.send('eth_getBalance', [f.wallets[3].address, 'latest'])), before);
    const rejectTreasury = f.effect({ lifecycle: id('rejected-treasury'), tokenId: 103n, treasury: recipient });
    await assert.rejects(f.execute(rejectTreasury));
    assert.equal(await f.contract.everMinted(103n), false);
  } finally { await f.rpc.disconnect(); }
});
