# Solana SDK execution surfaces

Custodial NFT and credential operations use `AzoaClient` lifecycle methods, shared
with other chains. The server owns preparation, custody, atomic platform fees,
participant acceptance and recovery. API responses preserve the actual token standard:
normal Metaplex NFTs and frozen SPL credentials are distinct.

The browser `SolanaProvider` remains the wallet-directed SPL/native transaction
surface. It does not claim server custody, issuer co-signature, frozen-credential
issuance or durable server-ledger orchestration. Those capabilities are N/A here,
not an invented browser capability. Native/SPL build/sign/broadcast and browser DEX
adapters remain independently tested. Server bridge availability must be discovered
from route capabilities; a browser builder is not proof that a server bridge leg is
implemented.

Server capability evidence and remaining standalone metadata/ATA management work
are documented in `Providers/Blockchain/Solana/AGENTS.md`. The shared SDK lifecycle
client is the mirrored contract for server NFT operations and supports historical
network/contract identity on credential revocation.
