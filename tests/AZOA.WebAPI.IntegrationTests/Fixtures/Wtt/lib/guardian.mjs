import { randomBytes } from 'node:crypto';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fixtureRoot, pins } from './upstream.mjs';

// Local guardian set and VAA v1 codec (body digest = keccak256(keccak256(body))); see ../AGENTS.md §Guardian.
const require = createRequire(path.join(fixtureRoot, 'package.json'));
const { SigningKey, computeAddress, getBytes, hexlify, keccak256, zeroPadValue } = require('ethers');

/** Creates an ephemeral local guardian set; keys never belong to a public network. */
export function createGuardianSet(size = 1) {
  const keys = Array.from({ length: size }, () => new SigningKey(hexlify(randomBytes(32))));
  return { index: pins.guardian.guardianSetIndex, keys, addresses: keys.map(key => computeAddress(key.publicKey)) };
}

/** Restores a guardian set handed over through the private state file. */
export const restoreGuardianSet = state => ({
  index: state.index, keys: state.privateKeys.map(key => new SigningKey(key)), addresses: state.addresses,
});

const u16 = value => { const buffer = Buffer.alloc(2); buffer.writeUInt16BE(value); return buffer; };
const u32 = value => { const buffer = Buffer.alloc(4); buffer.writeUInt32BE(value); return buffer; };
const u64 = value => { const buffer = Buffer.alloc(8); buffer.writeBigUInt64BE(BigInt(value)); return buffer; };
const bytes32 = hex => Buffer.from(getBytes(zeroPadValue(hex.startsWith('0x') ? hex : '0x' + hex, 32)));

/** Encodes a VAA body exactly as the guardians observe it. */
export function encodeBody({ timestamp, nonce, emitterChain, emitterAddress, sequence, consistencyLevel, payload }) {
  return Buffer.concat([u32(timestamp), u32(nonce), u16(emitterChain), bytes32(emitterAddress), u64(sequence),
    Buffer.from([consistencyLevel]), Buffer.from(payload)]);
}

export const bodyDigest = body => keccak256(keccak256(body));

/** Signs a body with a strictly increasing subset of the guardian set and returns the full VAA bytes. */
export function signVaa(guardians, body, signerIndexes = guardians.keys.map((_, index) => index)) {
  const digest = bodyDigest(body);
  const signatures = signerIndexes.map(index => {
    const signature = guardians.keys[index].sign(digest);
    return Buffer.concat([Buffer.from([index]), Buffer.from(getBytes(signature.r)), Buffer.from(getBytes(signature.s)),
      Buffer.from([signature.yParity])]);
  });
  return Buffer.concat([Buffer.from([1]), u32(guardians.index), Buffer.from([signatures.length]), ...signatures, body]);
}

/** Governance VAA signed by the local set from the pinned governance emitter. */
export function governanceVaa(guardians, payload, sequence) {
  return signVaa(guardians, encodeBody({ timestamp: Math.floor(Date.now() / 1000), nonce: 0,
    emitterChain: pins.guardian.governanceChainId, emitterAddress: pins.guardian.governanceEmitter, sequence,
    consistencyLevel: 32, payload }));
}

/** Token bridge RegisterChain payload (module "TokenBridge", action 1, target chain 0 = every chain). */
export function registerChainPayload(emitterChain, emitterAddress) {
  const module = Buffer.alloc(32); Buffer.from('TokenBridge').copy(module, 32 - 'TokenBridge'.length);
  return Buffer.concat([module, Buffer.from([1]), u16(0), u16(emitterChain), bytes32(emitterAddress)]);
}

/** Serializes a guardian set for the private state handover to the C# parent. */
export const guardianState = guardians => ({ index: guardians.index, addresses: guardians.addresses,
  privateKeys: guardians.keys.map(key => key.privateKey) });
