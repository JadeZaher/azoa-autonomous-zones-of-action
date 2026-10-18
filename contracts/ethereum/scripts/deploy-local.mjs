import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { serveLocalChain } from './hardhat-network.mjs';
import { ContractFactory, JsonRpcProvider, TypedDataEncoder, Wallet, keccak256 } from 'ethers';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workspace = path.resolve(root, '../..');
const port = Number(process.env.AZOA_ETHEREUM_LOCAL_PORT ?? 8547);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid local RPC port.');
async function occupied(port) {
  return new Promise(resolve => {
    const socket = net.connect({ host: '127.0.0.1', port });
    socket.setTimeout(1500);
    socket.once('connect', () => { socket.destroy(); resolve(true); });
    socket.once('error', () => resolve(false));
    socket.once('timeout', () => { socket.destroy(); resolve(false); });
  });
}
if (await occupied(Number(process.env.AZOA_API_HOST_PORT ?? 5000)))
  throw new Error('Stop every local API/worker instance before setup; the managed API port is still listening.');
if (await occupied(port)) throw new Error('Local EVM port is occupied; setup never replaces an existing chain.');
const artifact = JSON.parse(fs.readFileSync(path.join(root, 'artifacts/AzoaNft.json'), 'utf8'));
const server = await serveLocalChain({ totalAccounts: 5, port });
const provider = new JsonRpcProvider(server.url, 31337, { staticNetwork: true });
const authority = new Wallet(server.keys[0], provider);
const contract = await new ContractFactory(artifact.abi, artifact.bytecode, authority).deploy(authority.address);
await contract.waitForDeployment();
const address = await contract.getAddress();
const deployed = await provider.getCode(address);
const template = Buffer.from(artifact.deployedBytecode.slice(2), 'hex');
const normalized = Buffer.from(deployed.slice(2), 'hex');
for (const references of Object.values(artifact.immutableReferences)) {
  for (const { start, length } of references) {
    if (start < 0 || length < 1 || start + length > normalized.length) throw new Error('Invalid immutable reference.');
    normalized.fill(0, start, start + length);
    template.fill(0, start, start + length);
  }
}
const deployment = await new ContractFactory(artifact.abi, artifact.bytecode, authority).getDeployTransaction(authority.address);
const probe = '0x01020304';
const digest = TypedDataEncoder.hash({ name: 'AZOA NFT', version: '1', chainId: 31337, verifyingContract: address },
  { Execution: [{ name: 'payloadHash', type: 'bytes32' }] }, { payloadHash: keccak256(probe) });
if (keccak256(template) !== keccak256(normalized) || await contract.credentialAuthority() !== authority.address
    || contract.deploymentTransaction().data !== deployment.data || await contract.authorizationDigest(probe) !== digest)
  throw new Error('Deployed code or immutable authority differs from the pinned artifact.');
const epoch = randomUUID();
const configuration = {
  AZOA: { Ethereum: { PlatformPrivateKey: authority.privateKey, AllocatorActivationEpoch: epoch,
    Nft: { Networks: { Devnet: { ContractAddress: address, RuntimeCodeHash: keccak256(deployed), CredentialAuthorityAddress: authority.address } } } } },
  Blockchain: { Mode: 'Real', DefaultChain: 'Ethereum', DefaultNetwork: 'Devnet', Chains: [
    { ChainType: 'Algorand', Devnet: { IsEnabled: false }, Testnet: { IsEnabled: false }, Mainnet: { IsEnabled: false } },
    { ChainType: 'Solana', Devnet: { IsEnabled: false }, Testnet: { IsEnabled: false }, Mainnet: { IsEnabled: false } },
    { ChainType: 'Ethereum', Devnet: { NodeUrl: `http://127.0.0.1:${port}`, IsEnabled: true, ChainId: '31337' },
      Testnet: { IsEnabled: false }, Mainnet: { IsEnabled: false } },
  ] },
  NftFees: { Policies: { '4': { ChainType: 'Ethereum', Network: 'Devnet', Revision: 1, TreasuryAddress: '',
    MintNativeBaseUnits: '0', TransferNativeBaseUnits: '0', BurnNativeBaseUnits: '0', RevokeNativeBaseUnits: '0', RefundNativeBaseUnits: '0' } } },
};
fs.writeFileSync(path.join(workspace, 'appsettings.EthereumLocal.json'), JSON.stringify(configuration, null, 2) + '\n', { mode: 0o600 });
console.log(`Fresh isolated EVM listening on 127.0.0.1:${port}; verified NFT deployment ${address}.`);
console.log('Development overlay saved (contains only this ephemeral local chain key). Start the API with AZOA_ETHEREUM_LOCAL=true.');
console.log(`Activate platform signer ${authority.address} through POST /api/operator/ethereum/recovery/Devnet/activate after API startup.`);
console.log('Keep this process running. Stop the API before stopping this isolated chain. No public-network deployment occurred.');
const stop = async () => { await server.close(); provider.destroy(); process.exit(0); };
process.once('SIGINT', stop);
process.once('SIGTERM', stop);
