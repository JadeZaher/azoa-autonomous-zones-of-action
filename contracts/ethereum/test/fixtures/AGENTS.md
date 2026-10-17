# Ethereum protocol fixture provenance

`WETH9.json` is the unmodified official Uniswap v3-periphery test deployment
artifact at commit `7c8367c896ec8514198a8c03e5b0668c8b207a79`, path
`test/contracts/WETH9.json`. Source URL:
https://raw.githubusercontent.com/Uniswap/v3-periphery/7c8367c896ec8514198a8c03e5b0668c8b207a79/test/contracts/WETH9.json

SHA256: `67f36a6b7174cc41af5e6e2bcf393d5da253c380c8de72becbe8c3173e34c8da`.
The protocol fixture checks this digest before deployment. Factory/pool and
router/quoter/position-manager artifacts come from exact lockfile dependencies
`@uniswap/v3-core@1.0.1` and `@uniswap/v3-periphery@1.4.4`.

The DEX fixture runs only an in-process Ganache chain with generated ephemeral
accounts. Its test ERC20 and callback receiver are local adversarial fixtures;
the pool and router under test are the actual pinned protocol artifacts. No
public RPC, inherited key, faucet or external transaction is used. Source
authoring does not establish deployment or settlement evidence until the final
integrated protocol test gate runs successfully.
