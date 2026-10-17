---
type: source-review
status: bounded-slices-accepted
reviewer: lane-B
---

# Direct NFT authority and pinned signer review

Independent source inspection of MintCore, command ActorAuthority, controller and
manager witness forwarding, shared original-authority fence, and authored mint
fixtures found the original credential/wallet retained through admission and
new preparation/first-send. Observation updates remain outside that permission.

One blocker was found: applying the new signing fence to all fresh claims broke
pre-admission cancellation tombstones, which intentionally have no wallet.
Root corrected this with NftCancellationTombstone matching the complete empty
terminal marker and exemption only on fresh claim. The correction was reread
and accepted; forged prepared markers and delayed admission have authored
real-store fixtures. No runtime verdict is implied.

The pinned PlatformAuthorityAddress delta was independently read in quotation,
mint command and NftLifecycleIntentFactory. Platform Create/Deliver/Revoke use
the saved address and refuse missing or rotated current configuration. They do
not silently switch signing identity. Full participant-stage and workflow
witness integration remains Root's declared implementation scope and was not
accepted by this bounded review. Build/test gates remain unrun.

## Participant stage delta

Independently inspected the exact request binding, preserved consent history,
saved signer tenant, original plus participant transaction fence, and same-stage
replay without replacing authority. No additional blocking source defect found
in that bounded admission/first-egress path. Unit and real-store fixtures are
authored, not executed. The public SDK carries the complete request and the panel
persists it before sending with account/operation response suppression.

Two follow-ups sent to Root: enforce unchanged existing asset/protocol/contract
identity across a consent append at the store boundary; expose required fresh
consent for a replacement Preparing generation whose current stageAcceptance is
absent. The latter currently has no panel button and belongs to the still-open
replacement cost/consent journey. This is not full NFT completion approval.

Root corrected both follow-ups and the reviewer reread them: consent append now
preserves exact prior source/asset/network/protocol/history fields; the replacement
disclosure flag requires a participant stage without current consent and exact
previous-generation no-effect evidence. The panel offers an explicit replacement
stage acceptance only under that flag. Bounded participant source verdict:
ACCEPTED. Runtime gates and separate replacement-cost implementation remain open.
