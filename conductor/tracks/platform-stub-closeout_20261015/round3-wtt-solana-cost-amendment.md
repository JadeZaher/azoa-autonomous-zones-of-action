---
type: design
---

# Solana WTT execution-time cost bounds

Status: independently accepted by the root reviewer with the clarifications below; source and protocol review remain required before capability activation.

The pinned token-bridge `transfer.rs` transfers `bridge.config.fee` from its payer during execution. A pre-send account read cannot enforce a frozen caller cap if governance changes that field before execution. Official source is captured in `.omc/research/round3-wtt-solana-transfer-process-2026-10-15.md`, particularly native transfer lines 229–234 and wrapped transfer lines 395–400. This amendment closes that gap without treating preflight as an on-chain condition.

## Authority and exact funding

The real consenting user remains the outer Solana transaction fee payer and the source token owner. A separate protected system account is the Wormhole instruction's payer. Its random Ed25519 seed is claimed once in the existing encrypted, versioned stage-key vault before message preparation. It is bound to protocol, actor, tenant, wallet, operation, phase, stage and generation. It cannot collide with the user's key or another stage role. Source publication retains a separate protected message account. Wrapped transfer uses the real owner's `from_owner` signer, independently of the bridge payer. Native SPL transfer uses only the delegate approval signed by that actual owner.

One signed atomic transaction contains the execution guard described below, an exact SystemProgram transfer from the real payer to the protected bridge payer, any fixed-amount wrapped-SOL deposit, the exact token approval, and the protocol instruction. No separate funding send precedes publication. The bridge-payer funding amount is frozen to the quoted protocol fee plus all quoted account rents for that stage, within the user's native-value and locked-deposit caps. Network transaction fees have their own reserved cap and actual payer. Every transfer amount is encoded in the original message. The isolated account bounds total user funding; it does not by itself enforce separate fee and rent categories. Unsolicited third-party credits do not enlarge an authorized user transfer, but they also must not permit an increased protocol fee.

## Atomic execution guard

A small concrete AZOA Solana guard program is required, with reproducible source, pinned deployed code and removed upgrade authority. It performs no transfer and no arbitrary CPI. The provider verifies the guard's immutable loader identity before quoting/preparing and includes it as the first stateful instruction after any required ComputeBudget prelude in the atomic transaction. The closed builder prohibits intervening config or upgrade instructions. Missing deployment/configuration does not license bypass; local protocol fixtures deploy this actual program.

Its closed versioned instruction binds the exact core Bridge address/owner, expected protocol fee, quoted Rent sysvar bytes, core/token program and program-data addresses, and both executable code hashes. It verifies those account owners, roles, loader links and hashes on-chain. The program-data deployment slots must precede the current Clock slot, avoiding same-slot loader activation ambiguity. Core config, Rent, Clock, program and program-data accounts are all explicit transaction accounts; Solana account locks prevent governance/config/code updates between this guard and the subsequent protocol instruction. The Rent comparison preserves the separately quoted rent calculation. The protocol fee must equal the frozen quote, even when a smaller actual rent requirement or third-party bridge-payer credit would otherwise permit a larger fee from the same total funding.

The provider freezes the actual account-creation sizes and protocol account roles used to calculate the rent ceiling. A prerequisite created in the interim can reduce actual rent and leave a residual, but cannot increase the fee allowance. Any unsupported schema/code change fails the guard or program pin rather than being interpreted as a compatible cost update. The quote and preparation include the guard instruction and its compute/network cost. Guard failure can spend the original outer network fee but atomically prevents user funding, token debit and publication.

The builder distinguishes real transaction payer, bridge payer, source owner, message key, and recipient. Shared-key role substitution is rejected. Target verification/posting/registration use the same bounded payer construction wherever the program can debit dynamic rent from its payer. Any stage whose required account schema cannot use this separation must be demonstrated from the official processor and handled explicitly before capability activation.

## Durable recovery and accounting

The winning unsigned preparation contains both protected public keys, every funding/approval/protocol instruction, exact fee/rent/value reservations, genesis, blockhash, context slot and validity height. The fill-once signed candidate is committed before any first-send authorization. The operation journal's authorization CAS fences cancellation and new preparation. Retry resubmits only that wire. A missing transaction or expired blockhash without complete authoritative history remains ambiguous; neither a fresh funding generation nor cleanup is authorized on that basis.

A finalized failed atomic transaction proves no bridge-payer funding or token effect from that transaction, while the actual outer network fee remains spent. A finalized successful source publication remains published even if later residual-account cleanup is pending or fails. Target redemption likewise remains redeemed independently of cleanup. Never represent cleanup failure as permission to republish or redeem another body.

An existing account or lower actual rent may leave lamports in the protected bridge payer. Both a protocol fee increase and a decrease fail exact guard equality. Residual funds remain a discoverable operation resource. Cleanup requires authoritative resolution of every wire that can debit the account, exact account ownership and its current balance, and a new immutable cleanup preparation transferring that exact balance only to the frozen original refund address. The actual user pays the cleanup network fee; the protected account signs its own transfer. A change in balance cannot widen the transfer encoded in the cleanup wire. An external credit after preparation may leave another residual amount; it does not authorize a different message under the same generation.

The original quote explicitly reserves a cleanup network fee and discloses bridge-payer funding, possible retained balance and refund destination. Cleanup first uses that reservation. If legitimately spent fees exhaust it, a separately quoted, explicitly consented linked recovery command is required; original caps never increase silently. Operation and resource version fences seal the predecessor before authorizing a replacement cleanup generation. Already-authorized ambiguous funding or cleanup blocks replacement. Cleanup is not a second economic bridge operation and cannot change asset, recipient, source body or target replay claim.

## Required evidence

- Actual provider fixture mutates protocol fee between quote/preparation and execution, including fee +1 below total funding when rent is cheaper/preexisting and the bridge payer has an unsolicited credit: the guard refuses, no user funding or token debit occurs, and no source message is published.
- Rent sysvar, program-data hash, loader link, guard upgrade authority and same-slot program deployment mutations fail closed; exact guard compute/network costs are included in the quote.
- A lower fee also fails exact guard equality. Lower actual rent leaves a visible, recoverable residual with separately reported spent network fee and actual refund.
- Actual runtime fixtures measure guard code hashing compute units, loaded-account sizes and complete transaction packet size under the supported runtime ceilings. Quote values come from that concrete message and measured simulation, never guessed guard costs.
- Wrapped transfer proves the real source owner's signature is required when bridge payer is different.
- Concurrent preparation and lost first response retain the same protected keys, unsigned plan, signed transaction and one funding effect.
- Ambiguous publication never triggers cleanup or new funding; successful publication remains successful while cleanup is pending.
- Cleanup overlap, lost response, balance changes, exhausted original fee budget, explicit linked quote and wallet-key rotation preserve identity and caps.

These are source and fixture requirements. No runtime checks have been executed.
## Registration metadata dependency amendment (independently accepted)

The actual pinned token bridge's `create_wrapped` invokes Metaplex
`create_metadata_accounts_v3`. Official Metaplex processor source levies a creation
fee separately from retained metadata rent: `minimum_balance(1308) + 5440` lamports,
then allocates the metadata account at its declared maximum size. Those constants
must be associated with the explicitly supported metadata executable identity;
an arbitrary configured executable cannot silently inherit this policy. Captures
are `round3-wtt-metadata-creation-fees-2026-10-15.md`,
`round3-wtt-metadata-fee-constants-2026-10-15.md` and
`round3-wtt-metadata-fee-policy-2026-10-15.md`. Immutable source revision and actual
local deployed executable correspondence remain required protocol fixture evidence.

Extend the existing guard with closed version2 for registration only: existing
90-byte frame plus metadata executable SHA256 (122 bytes total), existing eight
accounts plus the canonical Metaplex program and its actual ProgramData. Check
the same loader ownership, program-data link, prior-slot deployment and executable
hash under transaction read locks. Version1 remains the exact existing eight-account
frame. No optional arbitrary dependency list or caller-selected program is admitted.
The provider records the supported metadata fee policy and code pin in the quote,
puts the calculated levy under native value/spent protocol cost, and puts only
actual account minimum balances under locked deposits. The bounded protected payer
receives their sum once in the atomic registration transaction. Cheaper or already
allocated accounts can leave a residual but cannot authorize a different program
or fee policy. Actual post-balances distinguish retained deposit, metadata levy and
refundable protected payer residual.

Required adversarial protocol fixtures: metadata executable upgrade between quote
and execution is refused by the guard; old code with changed Rent is refused;
valid registration records rent and levy separately; precredited metadata/payer
does not increase authorized levy; actual v2 guard loaded bytes, compute and full
packet size fit runtime ceilings. Root accepted this design before implementation. Closed v2 guard source and instruction framing are authored; quote/provider integration and executed protocol evidence remain required. Capability is not enabled.

