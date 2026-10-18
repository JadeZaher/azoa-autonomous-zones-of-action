// Local-only Hardhat 2.26.3 network: random per-process accounts handed over in memory by hardhat-network.mjs (never disk). See AGENTS.md §Local chain.
const accounts = JSON.parse(process.env.AZOA_LOCAL_HARDHAT_ACCOUNTS ?? '[]');
if (accounts.length === 0) throw new Error('Load Hardhat through scripts/hardhat-network.mjs; it supplies the ephemeral accounts.');

module.exports = {
  networks: {
    hardhat: { chainId: 31337, hardfork: 'shanghai', accounts, mining: { auto: true }, loggingEnabled: false },
  },
};
