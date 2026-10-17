import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

// Loopback Hardhat 2.26.3 chain with fresh random accounts, shared by chain.mjs and dex-chain.mjs; see AGENTS.md §Hardhat.
const here = path.dirname(fileURLToPath(import.meta.url));
export const root = path.resolve(here, '../../../..');
export const contracts = path.join(root, 'contracts/ethereum');
export const require = createRequire(path.join(contracts, 'package.json'));

export async function startChain({ totalAccounts, balanceEther }) {
  const keys = Array.from({ length: totalAccounts }, () => '0x' + randomBytes(32).toString('hex'));
  const balance = (BigInt(balanceEther) * 10n ** 18n).toString();
  process.env.AZOA_FIXTURE_HARDHAT_ACCOUNTS = JSON.stringify(keys.map(privateKey => ({ privateKey, balance })));
  process.env.HARDHAT_CONFIG = path.join(here, 'hardhat.fixture.config.cjs');
  process.env.HARDHAT_NETWORK = 'hardhat';
  process.env.HARDHAT_DISABLE_TELEMETRY_PROMPT = 'true';
  const hre = require('hardhat');
  delete process.env.AZOA_FIXTURE_HARDHAT_ACCOUNTS;
  const { JsonRpcServer } = require('hardhat/internal/hardhat-network/jsonrpc/server');
  const server = new JsonRpcServer({ hostname: '127.0.0.1', port: 0, provider: hre.network.provider });
  const { port } = await server.listen();
  const url = `http://127.0.0.1:${port}`;
  const { JsonRpcProvider, Wallet } = require('ethers');
  const provider = new JsonRpcProvider(url, 31337, { staticNetwork: true });
  const wallets = keys.map(key => new Wallet(key, provider));
  return { url, provider, wallets, close: async () => { provider.destroy(); await server.close(); } };
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
