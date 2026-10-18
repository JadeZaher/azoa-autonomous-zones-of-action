import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

// Local Hardhat 2.26.3 chains (EIP-1898 + `finalized` capable) with fresh random accounts; see scripts/AGENTS.md.
const scripts = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(path.join(scripts, '..', 'package.json'));
let runtime;

function load(totalAccounts, balanceEther) {
  if (runtime) {
    if (runtime.keys.length < totalAccounts) throw new Error('Hardhat runtime already loaded with fewer accounts.');
    return runtime;
  }
  const keys = Array.from({ length: totalAccounts }, () => '0x' + randomBytes(32).toString('hex'));
  const balance = (BigInt(balanceEther) * 10n ** 18n).toString();
  process.env.AZOA_LOCAL_HARDHAT_ACCOUNTS = JSON.stringify(keys.map(privateKey => ({ privateKey, balance })));
  process.env.HARDHAT_CONFIG = path.join(scripts, 'hardhat.local.config.cjs');
  process.env.HARDHAT_NETWORK = 'hardhat';
  process.env.HARDHAT_DISABLE_TELEMETRY_PROMPT = 'true';
  const hre = require('hardhat');
  delete process.env.AZOA_LOCAL_HARDHAT_ACCOUNTS;
  runtime = { hre, keys };
  return runtime;
}

/** Fresh in-process EIP-1193 chain; each call gets its own genesis. */
export async function localChain({ totalAccounts = 5, balanceEther = 10000 } = {}) {
  const { hre, keys } = load(totalAccounts, balanceEther);
  const { createProvider } = require('hardhat/internal/core/providers/construction');
  const provider = await createProvider(hre.config, 'hardhat', hre.artifacts);
  return { provider, keys: keys.slice(0, totalAccounts) };
}

/** Fresh chain served over loopback HTTP on `port` (0 = random). */
export async function serveLocalChain({ totalAccounts = 5, balanceEther = 10000, port = 0 } = {}) {
  const { provider, keys } = await localChain({ totalAccounts, balanceEther });
  const { JsonRpcServer } = require('hardhat/internal/hardhat-network/jsonrpc/server');
  const server = new JsonRpcServer({ hostname: '127.0.0.1', port, provider });
  const address = await server.listen();
  return { url: `http://127.0.0.1:${address.port}`, keys, close: () => server.close() };
}
