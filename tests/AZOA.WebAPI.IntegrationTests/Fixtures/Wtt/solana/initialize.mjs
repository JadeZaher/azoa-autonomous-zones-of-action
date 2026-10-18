import path from 'node:path';
import { createRequire } from 'node:module';
import { fixtureRoot, pins, readState, requireLoopback, writeState } from '../lib/upstream.mjs';

// Initializes the pinned core and token bridge programs (loaded at their canonical ids with --bpf-program) with the
// local guardian set. Solitaire one-byte discriminants; accounts per the pinned SDK initialize instructions.
// Usage: `initialize.mjs --rpc <url>`. See ../AGENTS.md §Solana.
const require = createRequire(path.join(fixtureRoot, 'package.json'));
const { Connection, Keypair, LAMPORTS_PER_SOL, PublicKey, SYSVAR_CLOCK_PUBKEY, SYSVAR_RENT_PUBKEY, SystemProgram,
  Transaction, TransactionInstruction, sendAndConfirmTransaction } = require('@solana/web3.js');

const rpc = requireLoopback(process.argv[process.argv.indexOf('--rpc') + 1]);
const core = new PublicKey(pins.solana.programs.core.programId);
const tokenBridge = new PublicKey(pins.solana.programs.tokenBridge.programId);
const guardianSetExpirationSeconds = 86_400;
const messageFeeLamports = 100n;
const derive = (seeds, program) => PublicKey.findProgramAddressSync(seeds, program)[0];
const guardianSetSeed = index => { const buffer = Buffer.alloc(4); buffer.writeUInt32BE(index); return buffer; };

const connection = new Connection(rpc, 'confirmed');
for (const [role, program] of [['core', core], ['tokenBridge', tokenBridge]]) {
  const account = await connection.getAccountInfo(program);
  if (!account?.executable) throw new Error(`${role} program is not loaded at its canonical id; start the validator with --bpf-program.`);
}
const guardians = readState('guardians.json');
const payer = Keypair.generate();
await connection.confirmTransaction(await connection.requestAirdrop(payer.publicKey, 10 * LAMPORTS_PER_SOL), 'confirmed');

const bridgeConfig = derive([Buffer.from('Bridge')], core);
const guardianSet = derive([Buffer.from('GuardianSet'), guardianSetSeed(guardians.index)], core);
const feeCollector = derive([Buffer.from('fee_collector')], core);
const keys = guardians.addresses.map(address => Buffer.from(address.slice(2), 'hex'));
const coreData = Buffer.alloc(1 + 4 + 8 + 4 + 20 * keys.length);
coreData.writeUInt8(0, 0);
coreData.writeUInt32LE(guardianSetExpirationSeconds, 1);
coreData.writeBigUInt64LE(messageFeeLamports, 5);
coreData.writeUInt32LE(keys.length, 13);
keys.forEach((key, index) => key.copy(coreData, 17 + 20 * index));
const writable = pubkey => ({ pubkey, isSigner: false, isWritable: true });
const readonly = pubkey => ({ pubkey, isSigner: false, isWritable: false });
const coreInitialize = new TransactionInstruction({ programId: core, data: coreData, keys: [
  writable(bridgeConfig), writable(guardianSet), writable(feeCollector), { pubkey: payer.publicKey, isSigner: true, isWritable: true },
  readonly(SYSVAR_CLOCK_PUBKEY), readonly(SYSVAR_RENT_PUBKEY), readonly(SystemProgram.programId)] });

const tokenBridgeConfig = derive([Buffer.from('config')], tokenBridge);
const tokenBridgeInitialize = new TransactionInstruction({ programId: tokenBridge, data: Buffer.concat([Buffer.from([0]), core.toBuffer()]),
  keys: [{ pubkey: payer.publicKey, isSigner: true, isWritable: true }, writable(tokenBridgeConfig), readonly(SYSVAR_RENT_PUBKEY),
    readonly(SystemProgram.programId)] });

await sendAndConfirmTransaction(connection, new Transaction().add(coreInitialize), [payer], { commitment: 'confirmed' });
await sendAndConfirmTransaction(connection, new Transaction().add(tokenBridgeInitialize), [payer], { commitment: 'confirmed' });

for (const [role, address, owner] of [['bridge config', bridgeConfig, core], ['guardian set', guardianSet, core],
  ['token bridge config', tokenBridgeConfig, tokenBridge]]) {
  const account = await connection.getAccountInfo(address);
  if (!account?.owner.equals(owner)) throw new Error(`${role} was not created by its program.`);
}
const storedSet = (await connection.getAccountInfo(guardianSet)).data;
if (!keys.every(key => storedSet.includes(key))) throw new Error('Guardian set account does not hold the local guardian keys.');
const emitter = derive([Buffer.from('emitter')], tokenBridge);
writeState('solana.json', { rpc, wormholeChainId: pins.solana.wormholeChainId, genesisHash: await connection.getGenesisHash(),
  core: core.toBase58(), tokenBridge: tokenBridge.toBase58(), bridgeConfig: bridgeConfig.toBase58(), guardianSet: guardianSet.toBase58(),
  tokenBridgeConfig: tokenBridgeConfig.toBase58(), tokenBridgeEmitter: emitter.toBase58(),
  tokenBridgeEmitterHex: emitter.toBuffer().toString('hex'), messageFeeLamports: messageFeeLamports.toString(), registered: {} });
console.log(JSON.stringify(readState('solana.json')));
