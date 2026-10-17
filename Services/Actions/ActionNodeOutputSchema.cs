using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>JSON type of a declared node-output field (presence+type validation primitive).</summary>
public enum OutputFieldType
{
    String,
    Number,
    Boolean,
    Object,
    Array,
    Unknown
}

/// <summary>
/// The statically-known shape of the JSON a handler writes to
/// <see cref="ActionNodeExecution.Output"/>. See Services/Actions/AGENTS.md §output-schema.
/// </summary>
/// <remarks>
/// One of three cases:
/// <list type="bullet">
///   <item><b>Known</b> (<c>Open == false</c>, non-empty <see cref="Fields"/>): the
///   top-level JSON object carries exactly these fields with these types. A
///   <c>$from</c> path whose first segment is not a declared field fails presence.</item>
///   <item><b>No readable outputs</b> (<c>Open == false</c>, empty <see cref="Fields"/>):
///   the handler sets no <c>Output</c> (pure side-effect). Any <c>$from</c> into it
///   MUST fail presence.</item>
///   <item><b>Open</b> (<c>Open == true</c>): the output is free-form / not statically
///   known (opaque tenant payload, dynamic keys). The validator skips type-checking
///   and admits any path by ancestry.</item>
/// </list>
/// </remarks>
public sealed record NodeOutputShape(
    bool Open,
    IReadOnlyDictionary<string, OutputFieldType> Fields)
{
    /// <summary>An opaque / free-form output whose shape is not statically known.</summary>
    public static readonly NodeOutputShape OpenShape =
        new(Open: true, Fields: new Dictionary<string, OutputFieldType>());

    /// <summary>A pure side-effect node that writes no readable output.</summary>
    public static readonly NodeOutputShape None =
        new(Open: false, Fields: new Dictionary<string, OutputFieldType>());

    /// <summary>A known output object with the given top-level field→type map.</summary>
    public static NodeOutputShape Of(params (string Name, OutputFieldType Type)[] fields) =>
        new(Open: false, Fields: fields.ToDictionary(f => f.Name, f => f.Type));
}

/// <summary>
/// Authoritative per-<see cref="ActionNodeType"/> declaration of the top-level JSON
/// shape each handler serializes to <see cref="ActionNodeExecution.Output"/>. Lets a
/// validator prove <c>$from</c> bindings (presence + type) against real fields.
/// See Services/Actions/AGENTS.md §output-schema for the sync contract.
/// </summary>
/// <remarks>
/// Field names are PascalCase because the repo serializes with
/// <see cref="ActionNodeJson.Options"/>, which sets NO <c>PropertyNamingPolicy</c>
/// (only <c>PropertyNameCaseInsensitive</c>) — System.Text.Json therefore emits the
/// C# property names verbatim. Do NOT camelCase these.
/// <para>
/// Most handlers serialize the WHOLE <c>AZOAResult&lt;T&gt;</c>, so their top-level
/// shape is the wrapper — <c>IsError</c>/<c>Message</c>/<c>Result</c>/<c>Detail</c> —
/// with the domain payload nested under <c>Result</c> (an Object, or Array for the
/// list-returning query/traversal handlers). Deep validation into <c>Result.&lt;field&gt;</c>
/// is a follow-up; this catalog is faithful at the top level. A handful serialize a
/// domain object DIRECTLY (Bridge/Back) or a fixed literal (GateCheck) — those are
/// flat and fully typed here.
/// </para>
/// </remarks>
public static class ActionNodeOutputSchema
{
    /// <summary>Top-level fields of a serialized <c>AZOAResult&lt;T&gt;</c> wrapper.</summary>
    private static NodeOutputShape Wrapped(OutputFieldType resultType) => NodeOutputShape.Of(
        ("IsError", OutputFieldType.Boolean),
        ("Message", OutputFieldType.String),
        ("Result", resultType),
        ("Detail", OutputFieldType.Object));

    /// <summary>Wrapper whose <c>Result</c> is a single domain object.</summary>
    private static NodeOutputShape WrappedObject() => Wrapped(OutputFieldType.Object);

    /// <summary>Wrapper whose <c>Result</c> is a list (<c>IEnumerable&lt;T&gt;</c>).</summary>
    private static NodeOutputShape WrappedArray() => Wrapped(OutputFieldType.Array);

    /// <summary>Wrapper whose <c>Result</c> is a scalar (bool/int).</summary>
    private static NodeOutputShape WrappedScalar(OutputFieldType t) => Wrapped(t);

    private static readonly IReadOnlyDictionary<ActionNodeType, NodeOutputShape> _map =
        new Dictionary<ActionNodeType, NodeOutputShape>
        {
            // ── Holon operations (serialize whole AZOAResult<T>) ──
            // Result = IHolon
            [ActionNodeType.HolonCreate] = WrappedObject(),
            [ActionNodeType.HolonUpdate] = WrappedObject(),
            [ActionNodeType.HolonGet] = WrappedObject(),
            [ActionNodeType.HolonInteract] = WrappedObject(),
            [ActionNodeType.HolonClone] = WrappedObject(),
            // Result = bool
            [ActionNodeType.HolonDelete] = WrappedScalar(OutputFieldType.Boolean),
            [ActionNodeType.HolonMoveSubtree] = WrappedScalar(OutputFieldType.Boolean),
            // Result = int (affected count)
            [ActionNodeType.HolonPropagate] = WrappedScalar(OutputFieldType.Number),
            // Result = IEnumerable<IHolon>
            [ActionNodeType.HolonQuery] = WrappedArray(),
            [ActionNodeType.HolonGetChildren] = WrappedArray(),
            [ActionNodeType.HolonGetPeers] = WrappedArray(),
            [ActionNodeType.HolonGetAncestors] = WrappedArray(),
            [ActionNodeType.HolonGetDescendants] = WrappedArray(),
            // Result = HolonComposition
            [ActionNodeType.HolonCompose] = WrappedObject(),

            // ── NFT operations (serialize whole AZOAResult<T>) ──
            // Result = IBlockchainOperation (mint/transfer/burn)
            [ActionNodeType.NftMint] = WrappedObject(),
            [ActionNodeType.NftTransfer] = WrappedObject(),
            [ActionNodeType.NftBurn] = WrappedObject(),
            // Result = INft (view over IHolon)
            [ActionNodeType.NftGet] = WrappedObject(),
            // Result = IEnumerable<INft>
            [ActionNodeType.NftQuery] = WrappedArray(),
            // Result = NftMetadata
            [ActionNodeType.NftGetMetadata] = WrappedObject(),

            // ── Wallet operations (serialize whole AZOAResult<T>) ──
            // Result = IWallet
            [ActionNodeType.WalletCreate] = WrappedObject(),
            [ActionNodeType.WalletUpdate] = WrappedObject(),
            [ActionNodeType.WalletGet] = WrappedObject(),
            // Result = bool
            [ActionNodeType.WalletDelete] = WrappedScalar(OutputFieldType.Boolean),
            [ActionNodeType.WalletSetDefault] = WrappedScalar(OutputFieldType.Boolean),
            // Result = IEnumerable<IWallet>
            [ActionNodeType.WalletQuery] = WrappedArray(),
            // Result = PortfolioResult
            [ActionNodeType.WalletGetPortfolio] = WrappedObject(),

            // ── Action graph bundle operations (serialize whole AZOAResult<IActionGraphBundle>) ──
            [ActionNodeType.ActionGraphBundleGenerate] = WrappedObject(),
            [ActionNodeType.ActionGraphBundleDeploy] = WrappedObject(),

            // ── Search (serialize whole AZOAResult<SearchResult>) ──
            [ActionNodeType.Search] = WrappedObject(),

            // ── User NFT (serialize whole AZOAResult<UserNFTCompositeResult>) ──
            [ActionNodeType.UserNFTGetComposite] = WrappedObject(),

            // ── Blockchain (serialize whole safe AZOAResult<BlockchainOperationResponse>) ──
            // The nested Result is a strict allowlist; internal provider parameters
            // and idempotency/initiator metadata never become durable output.
            [ActionNodeType.BlockchainExecute] = WrappedObject(),

            // ── Internal / control-flow ──
            // Condition: passes context.Node.Config through verbatim (arbitrary config JSON).
            [ActionNodeType.Condition] = NodeOutputShape.OpenShape,
            // ComposeOutputs: a { <upstreamNodeName>: <outputJsonString> } map — dynamic keys.
            [ActionNodeType.ComposeOutputs] = NodeOutputShape.OpenShape,

            // ── Holon-transformation nodes (economic-primitive-nodes) ──
            // GateCheck: fixed literal {"pass":true} on success.
            [ActionNodeType.GateCheck] = NodeOutputShape.Of(("pass", OutputFieldType.Boolean)),
            // Emit: serializes the tenant-shaped cfg.Payload directly (free-form).
            [ActionNodeType.Emit] = NodeOutputShape.OpenShape,

            // Swap: flat SwapActionOutput projected only from the confirmed settlement receipt.
            [ActionNodeType.Swap] = NodeOutputShape.Of(
                ("OperationId", OutputFieldType.String), ("ChainType", OutputFieldType.String), ("Network", OutputFieldType.String),
                ("Provider", OutputFieldType.String), ("InputAssetId", OutputFieldType.String), ("OutputAssetId", OutputFieldType.String),
                ("ActualInput", OutputFieldType.String), ("ActualOutput", OutputFieldType.String), ("PlatformFeePaid", OutputFieldType.String),
                ("TransactionIds", OutputFieldType.Array), ("TransactionId", OutputFieldType.String), ("RetainedEffects", OutputFieldType.Array)),
            // Tier-2 economic nodes serialize the WHOLE AZOAResult<T>.
            // Result = strict BlockchainOperationResponse (Grant / Transfer / Refund)
            [ActionNodeType.Grant] = WrappedObject(),
            [ActionNodeType.Transfer] = WrappedObject(),
            [ActionNodeType.Refund] = WrappedObject(),
            // Result = strict FungibleTokenResultResponse
            [ActionNodeType.FungibleTokenCreate] = WrappedObject(),

            // ── Fractionalization rails — serialize r.Result DIRECTLY (unwrapped) ──
            // Both write the flat BridgeTransactionResult shape (NOT the AZOAResult wrapper).
            [ActionNodeType.Bridge] = BridgeTransactionShape,
            [ActionNodeType.Back] = BridgeTransactionShape,

            // ── Phase 5 D19 credential rail — serializes CredentialRevokeResult
            //    DIRECTLY (r.Result, not the AZOAResult wrapper). ──
            [ActionNodeType.CredentialRevoke] = CredentialRevokeShape,

            // ── Phase 5 D20 governance rails — each handler serializes a
            //    fixed-key Dictionary<string, object?>, so the keys are the
            //    literal camelCase names the handler writes (NOT PascalCase:
            //    dictionary keys bypass property naming entirely). ──
            [ActionNodeType.GovernanceProposalCreate] = GovernanceProposalCreateShape,
            [ActionNodeType.GovernanceVote] = GovernanceVoteShape,
            [ActionNodeType.GovernanceTally] = GovernanceTallyShape,

            // ── Phase 5 D21 credit rails — all three serialize the same
            //    LoanBalanceProjectionResult, whose [JsonPropertyName] attributes
            //    pin camelCase names regardless of ActionNodeJson.Options. ──
            [ActionNodeType.LoanOpen] = LoanBalanceProjectionShape,
            [ActionNodeType.LoanRepaymentRecord] = LoanBalanceProjectionShape,
            [ActionNodeType.LoanBalance] = LoanBalanceProjectionShape,

            // ── D14 / W3.4 hold rails — all four serialize the same flat
            //    Handlers.HoldNodeResult, whose [JsonPropertyName] attributes pin
            //    PascalCase names so a downstream $from binding reads identical
            //    fields whichever hold node produced them. ──
            [ActionNodeType.Hold] = HoldNodeShape,
            [ActionNodeType.Release] = HoldNodeShape,
            [ActionNodeType.HoldRefund] = HoldNodeShape,
            [ActionNodeType.ReleaseIntoHold] = HoldNodeShape,
        };

    /// <summary>
    /// The flat <c>Handlers.HoldNodeResult</c> shape (D14 / W3.4). Every field is
    /// non-null — empty string for "none" — so a binding never has to distinguish an
    /// absent field from a null one.
    /// </summary>
    private static NodeOutputShape HoldNodeShape => NodeOutputShape.Of(
        ("HoldId", OutputFieldType.String),
        ("ChainType", OutputFieldType.String),
        ("AssetId", OutputFieldType.String),
        ("Amount", OutputFieldType.String),
        ("State", OutputFieldType.String),
        ("BeneficiaryUserId", OutputFieldType.String),
        ("ReleaseNotBefore", OutputFieldType.String),
        ("NextHoldId", OutputFieldType.String),
        ("TransactionId", OutputFieldType.String),
        ("Replayed", OutputFieldType.Boolean),
        ("HoldRecordId", OutputFieldType.String),
        ("NextHoldRecordId", OutputFieldType.String),
        ("OperationId", OutputFieldType.String));

    /// <summary>
    /// The flat <c>BridgeTransactionResult</c> shape — Bridge/Back serialize
    /// <c>r.Result</c> directly, so this is the top-level output object (no wrapper).
    /// </summary>
    private static NodeOutputShape BridgeTransactionShape => NodeOutputShape.Of(
        ("Id", OutputFieldType.String),
        ("UserId", OutputFieldType.String),           // Guid → JSON string
        ("SourceChain", OutputFieldType.String),
        ("TargetChain", OutputFieldType.String),
        ("SourceTokenId", OutputFieldType.String),
        ("TargetTokenId", OutputFieldType.String),
        ("SourceAddress", OutputFieldType.String),
        ("TargetAddress", OutputFieldType.String),
        ("Amount", OutputFieldType.String),
        ("Status", OutputFieldType.Number),             // BridgeStatus enum → int
        ("Mode", OutputFieldType.Number),               // BridgeMode enum → int
        ("LockTxHash", OutputFieldType.String),
        ("MintTxHash", OutputFieldType.String),
        ("ProofData", OutputFieldType.String),
        ("ErrorMessage", OutputFieldType.String),
        ("CreatedAt", OutputFieldType.String),          // DateTime → ISO string
        ("CompletedAt", OutputFieldType.String),
        ("WormholeEmitterChainId", OutputFieldType.Number),
        ("WormholeEmitterAddress", OutputFieldType.String),
        ("WormholeSequence", OutputFieldType.Number),
        ("VaaBytes", OutputFieldType.String),
        ("VaaSignatureCount", OutputFieldType.Number),
        ("RedemptionTxHash", OutputFieldType.String),
        ("Network", OutputFieldType.Number));           // ChainNetwork enum → int

    /// <summary>
    /// The flat <c>CredentialRevokeResult</c> shape (D19) — the handler serializes
    /// <c>r.Result</c> directly, so this is the whole top-level object. PascalCase
    /// because the DTO carries no <c>[JsonPropertyName]</c>. <c>IsConfirmed</c> is a
    /// computed get-only property and IS serialized, so it must be declared here or
    /// the closed-shape check sees an undeclared field.
    /// </summary>
    private static NodeOutputShape CredentialRevokeShape => NodeOutputShape.Of(
        ("AssetId", OutputFieldType.String),
        ("CredentialId", OutputFieldType.String),        // Guid → JSON string
        ("IssuerUserId", OutputFieldType.String),        // Guid → JSON string
        ("HolderUserId", OutputFieldType.String),        // Guid → JSON string
        ("HolderAddress", OutputFieldType.String),
        ("RecipientAddress", OutputFieldType.String),
        ("ChainType", OutputFieldType.String),
        ("Status", OutputFieldType.String),              // OperationStatus constant
        ("IsConfirmed", OutputFieldType.Boolean),
        ("TxHash", OutputFieldType.String),
        ("OperationId", OutputFieldType.String),         // Guid → JSON string
        ("LifecycleId", OutputFieldType.String),         // durable NFT lifecycle id
        ("LifecycleState", OutputFieldType.String),
        ("Replayed", OutputFieldType.Boolean));

    /// <summary>
    /// GovernanceProposalCreate output (D20): the literal dictionary the handler
    /// builds. <c>options</c> is the declared option list; <c>opensAt</c>/<c>closesAt</c>
    /// are read back out of proposal metadata and may serialize null (a null is
    /// type-compatible with any declared field).
    /// </summary>
    private static NodeOutputShape GovernanceProposalCreateShape => NodeOutputShape.Of(
        ("proposalId", OutputFieldType.String),
        ("status", OutputFieldType.String),
        ("options", OutputFieldType.Array),
        ("opensAt", OutputFieldType.String),
        ("closesAt", OutputFieldType.String),
        ("uniquenessOperationKey", OutputFieldType.String),
        ("uniquenessScope", OutputFieldType.String),
        ("createdAt", OutputFieldType.String));

    /// <summary>GovernanceVote output (D20): the ballot receipt, one holon id per key.</summary>
    private static NodeOutputShape GovernanceVoteShape => NodeOutputShape.Of(
        ("proposalId", OutputFieldType.String),
        ("voteId", OutputFieldType.String),
        ("option", OutputFieldType.String),
        ("castAt", OutputFieldType.String));

    /// <summary>
    /// GovernanceTally output (D20). <c>counts</c> is an option→int map with
    /// tenant-declared keys, hence Object rather than a declared field set;
    /// <c>tallyHash</c> is the recomputable digest over the counted ballots.
    /// </summary>
    private static NodeOutputShape GovernanceTallyShape => NodeOutputShape.Of(
        ("proposalId", OutputFieldType.String),
        ("totalVotes", OutputFieldType.Number),
        ("counts", OutputFieldType.Object),
        ("tallyHash", OutputFieldType.String),
        ("tallyAt", OutputFieldType.String),
        ("closesAt", OutputFieldType.String));

    /// <summary>
    /// The flat <c>LoanBalanceProjectionResult</c> shape (D21), written by LoanOpen,
    /// LoanRepaymentRecord and LoanBalance alike. Every money field is a base-unit
    /// decimal STRING — never a JSON number — so no consumer can round a balance.
    /// </summary>
    private static NodeOutputShape LoanBalanceProjectionShape => NodeOutputShape.Of(
        ("loanId", OutputFieldType.String),
        ("principal", OutputFieldType.String),
        ("totalRepaid", OutputFieldType.String),
        ("totalWrittenOff", OutputFieldType.String),
        ("outstanding", OutputFieldType.String),
        ("status", OutputFieldType.String),
        ("entryCount", OutputFieldType.Number));

    /// <summary>
    /// The declared output shape for <paramref name="nodeType"/>. Throws
    /// <see cref="NotSupportedException"/> for any unmapped type so a newly-added
    /// node type cannot silently skip its schema (mirrors
    /// <see cref="ActionNodeConfigRegistry.GetConfigType"/>).
    /// </summary>
    public static NodeOutputShape GetShape(ActionNodeType nodeType)
    {
        if (!_map.TryGetValue(nodeType, out var shape))
            throw new NotSupportedException(
                $"ActionNodeType.{nodeType} has no output-schema entry. Add it to ActionNodeOutputSchema.");
        return shape;
    }
}
