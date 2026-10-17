// In-process Hardhat 2.26.3 network for the loopback protocol fixtures; keys come from the parent process env (never disk). See AGENTS.md §Hardhat.
const accounts = JSON.parse(process.env.AZOA_FIXTURE_HARDHAT_ACCOUNTS ?? '[]');
if (accounts.length === 0) throw new Error('hardhat-chain.mjs must set AZOA_FIXTURE_HARDHAT_ACCOUNTS before loading Hardhat.');

module.exports = {
  networks: {
    hardhat: {
      chainId: 31337,
      hardfork: 'shanghai',
      accounts,
      mining: { auto: true },
      loggingEnabled: false,
    },
  },
};
