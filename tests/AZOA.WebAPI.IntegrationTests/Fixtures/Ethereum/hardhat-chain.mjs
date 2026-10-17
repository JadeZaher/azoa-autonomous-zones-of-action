import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Loopback Hardhat chain shared by chain.mjs and dex-chain.mjs (contracts/ethereum/scripts/hardhat-network.mjs); see AGENTS.md.
export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
export const contracts = path.join(root, 'contracts/ethereum');
export const require = createRequire(path.join(contracts, 'package.json'));
const { serveLocalChain } = await import(pathToFileURL(path.join(contracts, 'scripts/hardhat-network.mjs')).href);

export async function startChain({ totalAccounts, balanceEther }) {
  const served = await serveLocalChain({ totalAccounts, balanceEther });
  const { JsonRpcProvider, Wallet } = require('ethers');
  const provider = new JsonRpcProvider(served.url, 31337, { staticNetwork: true });
  const wallets = served.keys.map(key => new Wallet(key, provider));
  return { url: served.url, provider, wallets, close: async () => { provider.destroy(); await served.close(); } };
}

// The parent consumes stdout as a private pipe and closes stdin to stop the chain.
export function serveUntilParentCloses(chain, handshake) {
  process.stdout.write(JSON.stringify(handshake) + '\n');
  process.stdin.resume();
  let closing = false;
  const close = async () => {
    if (closing) return;
    closing = true;
    await chain.close();
    process.exit(0);
  };
  process.stdin.once('end', close);
  process.once('SIGTERM', close);
  process.once('SIGINT', close);
}
