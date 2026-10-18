// Loopback Hardhat node for the WTT six-route fixture (chain id 31337, finalized tag + EIP-1898 supported); see ../AGENTS.md §EVM.
module.exports = { networks: { hardhat: { chainId: 31337, hardfork: 'cancun', mining: { auto: true } } } };
