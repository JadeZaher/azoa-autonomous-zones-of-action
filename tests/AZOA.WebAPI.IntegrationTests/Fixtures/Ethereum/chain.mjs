import fs from 'node:fs';
import path from 'node:path';
import { contracts, require, serveUntilParentCloses, startChain } from './hardhat-chain.mjs';

// Loopback Hardhat chain with the compiled production AzoaNft; see AGENTS.md.
const { ContractFactory, keccak256 } = require('ethers');
const artifact = JSON.parse(fs.readFileSync(path.join(contracts, 'artifacts/AzoaNft.json'), 'utf8'));
const chain = await startChain({ totalAccounts: 4, balanceEther: 1000 });
const accounts = chain.wallets;
const contract = await new ContractFactory(artifact.abi, artifact.bytecode, accounts[0]).deploy(accounts[0].address);
await contract.waitForDeployment();
const address = await contract.getAddress();
// Ephemeral keys travel only over the private pipe; never written to disk.
serveUntilParentCloses(chain, { url: chain.url, address, codeHash: keccak256(await chain.provider.getCode(address)),
  keys: accounts.map(account => account.privateKey) });
