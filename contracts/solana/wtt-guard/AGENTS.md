# Solana WTT cost and executable guard

This concrete no-CPI program checks the quoted protocol fee, Rent sysvar and exact
core/token program-data hashes under the same transaction account locks as the WTT
instruction. It transfers no funds, writes no account, accepts no arbitrary CPI,
and accepts only the closed frames documented below. Deploy it with removed upgrade authority;
the provider must independently pin its code and immutable loader identity.

The instruction is exactly 90 bytes: version1, fee u64 little-endian, Rent bytes17,
core executable SHA25632, token executable SHA25632. Account order is Bridge,
Rent, Clock, core program, core program-data, token program, token program-data,
token config. The config must be token-program-owned and bind the actual core key.
Every account key is already committed by the signed compiled message; the provider
derives exact Bridge/config/program-data PDAs and reconstructs the complete list.
The guard checks program-data links, owners and hashes without repeating account
keys in instruction data, preserving packet capacity.
The two program-data deployment slots must precede Clock.slot. The closed provider
builder puts the guard first after a ComputeBudget prelude and forbids intervening
config/upgrade instructions. An exact fee decrease also fails; the guard never
silently reallocates rent allowance into protocol fees.

The dependency-free aligned account decoder follows the official Solana SDK
v1.18.26 `entrypoint::deserialize`, preserved by the supported Agave loader. It
accepts the exact distinct-account count for each closed frame, bounds each data length to the runtime
10MiB maximum and retains the ABI's 10KiB growth padding, 8-byte alignment and rent
epoch. Only runtime-created entrypoint memory is decoded; this is not a public
arbitrary byte-buffer parser. The SHA256 syscall uses the official slice-pointer/
length ABI. Captures: `.omc/research/round3-wtt-solana-entrypoint-abi-2026-10-15.md`
and `round3-wtt-solana-sha256-syscall-2026-10-15.md`.

Build with the existing checksum-verified Agave3.1.8/platform-tools1.52/Rust1.89
fixture toolchain, SBPFv0 and the checked-in dependency-free lockfile. Actual ELF
build, program deployment, immutable-authority check, compute consumption, loaded
account size and complete transaction packet size are mandatory final protocol
gates. Source authoring has not run them. Do not enable WTT capability merely
because this program source exists; provider preparation and receipt verification
must include the exact guard and actual deployed identity.

Final-sweep build commands (not run during source authoring):

```powershell
podman build -t localhost/azoa-wtt-guard:agave3.1.8 -f contracts/solana/wtt-guard/Containerfile contracts/solana/wtt-guard
podman create --name azoa-wtt-guard-export localhost/azoa-wtt-guard:agave3.1.8 /out/azoa_wtt_guard.so
podman cp azoa-wtt-guard-export:/out .omc/wtt-guard-elf
$env:AZOA_SOLANA_WTT_GUARD_ELF = (Resolve-Path .omc/wtt-guard-elf/azoa_wtt_guard.so).Path
```

Inspect existing container names and reuse a matching export container. Local
fixtures must require a numeric HTTP loopback RPC and independently pinned genesis
before deployment or funding. The fixture records ELF SHA256, deployed padded
ProgramData SHA256, removed authority, guard simulation units and full message
packet lengths. Compilation alone is not the final protocol gate.
# Token bridge configuration

The pinned token bridge supports 32-byte legacy and 137-byte migrated configuration
accounts. The guard binds the core address in the leading 32 bytes and requires the
migrated pause byte to be exactly zero. It does not infer unpaused state from a past
expiry timestamp. The source is captured in
`.omc/research/round3-wtt-solana-token-types-2026-10-15.md`.

## Registration dependency guard

Version 2 accepts exactly ten accounts and 122 bytes: the unchanged v1 eight roles
and 90-byte field layout (version byte 2), followed by the canonical Metaplex
program, its loader-linked ProgramData, and the metadata executable SHA256. Both
frames reject duplicate account references, extra accounts and trailing bytes.
The metadata executable receives the same ownership, prior-slot and SHA256 checks
as core/token bridge. The v2 guard does not itself infer a source revision from a
hash: provider admission requires the separately reviewed code/policy catalogue.
No arbitrary dependencies or alternate metadata program may be supplied.

## Metadata-only frame

Version 3 is exactly 50 bytes: version, Rent bytes17, metadata executable SHA25632. Its four distinct readonly nonsigner accounts are Rent, Clock, canonical Metaplex program and its ProgramData. It checks the same loader linkage, prior-slot deployment and executable hash as registration version2, without unrelated bridge accounts. The closed native builder places this guard immediately before metadata creation. The pinned immutable guard must be rebuilt and its new actual executable hash provisioned; old guards do not support this frame. Actual runtime rejection/compute/packet fixtures remain mandatory.

## DEX frame

Version 4 is exactly 82 bytes: version, Rent bytes17, Jupiter executable SHA25632, Whirlpool executable SHA25632. Its six distinct readonly nonsigner accounts are Rent, Clock, canonical Jupiter V6 program and ProgramData, canonical Whirlpool program and ProgramData. It reuses the existing prior-slot loader-link/hash checks and compares exact Rent under transaction locks. The provider inserts it after compute instructions and before any ATA setup/route; no upgrade or sysvar mutation instruction is admitted. This prevents pre-read Rent or executable pins from being mistaken for execution-time authority. Original v1/v2/v3 frames retain their exact account counts and lengths. The rebuilt immutable executable must be independently pinned; source changes do not provision readiness. Actual changed-Rent/program refusal and packet/compute fixtures remain required at the final sweep.

## Zero-holding close guard

Opcode 5 has exactly two accounts and 97 bytes: opcode, token-account key, mint key, owner key. The token account is writable and nonsigning; the owner is the writable signer/payer in the composed CloseAccount message. It accepts only canonical legacy SPL ownership, initialized 165-byte state, strict 0/1 COption tags, exact keys, zero amount and absent/original-owner close authority. Native wrapped-SOL additionally requires current lamports equal its stored native reserve, rejecting unsynchronized incoming SOL. The program writes nothing; the following exact owner CloseAccount performs the authorized closure atomically. The decoder now reads (without changing offsets) the lamports field already present in the aligned account ABI. Existing closed frames retain their exact sizes and branches. Actual deployed execution for token/native amount, reserve, owner, close authority and malformed-option mutations remains a mandatory final-sweep gate.

## Rent-only frame

Opcode 6 is exactly 18 bytes: opcode plus the exact 17-byte Rent sysvar. Its only account is the canonical Rent sysvar, readonly and nonsigning, owned by the sysvar program and nonexecutable. It performs no CPI or state write and acquires no DEX/bridge executable dependency. Recipient-native-output composition places it immediately after ComputeBudget and admits no intervening Rent mutation. The decoder accepts one account only with this exact length; other frames remain unchanged. SolanaRentGuardProtocolTests deploys the independently hash-pinned local ELF with removed authority and exercises actual valid/changed/truncated/extended/opcode/account/privilege cases, recording runtime compute and packet size. This fixture is authored, not executed. Set AZOA_SOLANA_WTT_GUARD_ELF_SHA256 to the independently captured build hash for the final gate.
