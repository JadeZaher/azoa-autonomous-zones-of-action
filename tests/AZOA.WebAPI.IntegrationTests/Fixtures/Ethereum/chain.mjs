import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const require = createRequire(path.join(root, 'contracts/ethereum/package.json'));
const ganache = require('ganache');
const { ContractFactory, JsonRpcProvider, Wallet, keccak256 } = require('ethers');
const artifact = JSON.parse(fs.readFileSync(path.join(root, 'contracts/ethereum/artifacts/AzoaNft.json'), 'utf8'));
const server = ganache.server({ logging: { quiet: true }, chain: { chainId: 31337, hardfork: 'shanghai' }, wallet: { totalAccounts: 4 } });
await server.listen(0, '127.0.0.1');
const url = `http://127.0.0.1:${server.address().port}`;
const provider = new JsonRpcProvider(url, 31337, { staticNetwork: true });
const accounts = Object.values(server.provider.getInitialAccounts()).map(account => new Wallet(account.secretKey, provider));
const contract = await new ContractFactory(artifact.abi, artifact.bytecode, accounts[0]).deploy(accounts[0].address);
await contract.waitForDeployment();
const address = await contract.getAddress();
// The parent consumes this private pipe; ephemeral keys are never written to disk.
process.stdout.write(JSON.stringify({ url, address, codeHash: keccak256(await provider.getCode(address)),
  keys: accounts.map(account => account.privateKey) }) + '\n');
process.stdin.resume();
let closing = false;
const close = async () => {
  if (closing) return;
  closing = true;
  provider.destroy();
  await server.close();
  process.exit(0);
};
process.stdin.once('end', close);
process.once('SIGTERM', close);
process.once('SIGINT', close);
