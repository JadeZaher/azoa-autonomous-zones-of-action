---
type: Research Notes
title: Bridge facts for the next full-goal assessment
status: Preliminary source findings; Round 3 plan not yet approved
---

Coordinator inspected the existing Wormhole orchestration while Round 2 lanes authored their accepted packages. These findings supplement, not replace, the required fresh parallel Round 3 assessments.

- WormholeAdapter.ComputeVaaDigest hashes the entire signed VAA envelope for the local uniqueness key, explicitly distinct from its parsed body signing digest. Different valid guardian signature subsets over one message can produce different full-envelope hashes. The next design must bind replay identity to the canonical message/body and source emitter/sequence, not signature-envelope variability. Preserve existing exactly-once assertions and add valid alternate-envelope vectors.
- RedeemTransferAsync currently disguises VAA bytes as BridgeMintRequest.TokenUri, source asset as `seq:<sequence>` and amount as 1. A real WTT redemption requires a typed proof-bound transfer contract; the existing trusted-wrapped-mint interface cannot stand in for native token-bridge verification. Bind original token chain/address, normalized amount, recipient/target, configured bridge and finalized source publication.
- CrossChainBridgeService currently marks successful target return Completed immediately, with no submitted-versus-final distinction in BridgeSettlement. Its post-consume error path marks Failed despite an ambiguous target send. The future target journal must persist exact prepared wire before submit and distinguish pending, proven no-effect and ambiguous outcomes; consumed-message ownership must remain recoverable under the original operation.
- Existing reverse logic burns a platform wrapped representation and releases a platform vault. WTT locks/burns in protocol contracts and reverses through another protocol transfer. Do not route WTT reversal through the trusted-vault release contract or interpret a target burn alone as authority to release protocol-held assets.

Official WTT contract guide captured in `.omc/research/round3-wormhole-wtt-contracts-2026-10-15.md`, stash `round3-wtt-input`. It describes source transferTokens and target completeTransfer, amount normalization to eight decimals, and the distinct recipient-contract restriction for transfer-with-payload. Pin exact protocol source/SDK contracts during the implementing lane. No bridge source was changed and no protocol test ran for this note.
