using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest;

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
/// <see cref="QuestNodeExecution.Output"/>. See Services/Quest/AGENTS.md §output-schema.
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
/// Authoritative per-<see cref="QuestNodeType"/> declaration of the top-level JSON
/// shape each handler serializes to <see cref="QuestNodeExecution.Output"/>. Lets a
/// validator prove <c>$from</c> bindings (presence + type) against real fields.
/// See Services/Quest/AGENTS.md §output-schema for the sync contract.
/// </summary>
/// <remarks>
/// Field names are PascalCase because the repo serializes with
/// <see cref="QuestNodeJson.Options"/>, which sets NO <c>PropertyNamingPolicy</c>
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
public static class QuestNodeOutputSchema
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

    private static readonly IReadOnlyDictionary<QuestNodeType, NodeOutputShape> _map =
        new Dictionary<QuestNodeType, NodeOutputShape>
        {
            // ── Holon operations (serialize whole AZOAResult<T>) ──
            // Result = IHolon
            [QuestNodeType.HolonCreate] = WrappedObject(),
            [QuestNodeType.HolonUpdate] = WrappedObject(),
            [QuestNodeType.HolonGet] = WrappedObject(),
            [QuestNodeType.HolonInteract] = WrappedObject(),
            [QuestNodeType.HolonClone] = WrappedObject(),
            // Result = bool
            [QuestNodeType.HolonDelete] = WrappedScalar(OutputFieldType.Boolean),
            [QuestNodeType.HolonMoveSubtree] = WrappedScalar(OutputFieldType.Boolean),
            // Result = int (affected count)
            [QuestNodeType.HolonPropagate] = WrappedScalar(OutputFieldType.Number),
            // Result = IEnumerable<IHolon>
            [QuestNodeType.HolonQuery] = WrappedArray(),
            [QuestNodeType.HolonGetChildren] = WrappedArray(),
            [QuestNodeType.HolonGetPeers] = WrappedArray(),
            [QuestNodeType.HolonGetAncestors] = WrappedArray(),
            [QuestNodeType.HolonGetDescendants] = WrappedArray(),
            // Result = HolonComposition
            [QuestNodeType.HolonCompose] = WrappedObject(),

            // ── NFT operations (serialize whole AZOAResult<T>) ──
            // Result = IBlockchainOperation (mint/transfer/burn)
            [QuestNodeType.NftMint] = WrappedObject(),
            [QuestNodeType.NftTransfer] = WrappedObject(),
            [QuestNodeType.NftBurn] = WrappedObject(),
            // Result = INft (view over IHolon)
            [QuestNodeType.NftGet] = WrappedObject(),
            // Result = IEnumerable<INft>
            [QuestNodeType.NftQuery] = WrappedArray(),
            // Result = NftMetadata
            [QuestNodeType.NftGetMetadata] = WrappedObject(),

            // ── Wallet operations (serialize whole AZOAResult<T>) ──
            // Result = IWallet
            [QuestNodeType.WalletCreate] = WrappedObject(),
            [QuestNodeType.WalletUpdate] = WrappedObject(),
            [QuestNodeType.WalletGet] = WrappedObject(),
            // Result = bool
            [QuestNodeType.WalletDelete] = WrappedScalar(OutputFieldType.Boolean),
            [QuestNodeType.WalletSetDefault] = WrappedScalar(OutputFieldType.Boolean),
            // Result = IEnumerable<IWallet>
            [QuestNodeType.WalletQuery] = WrappedArray(),
            // Result = PortfolioResult
            [QuestNodeType.WalletGetPortfolio] = WrappedObject(),

            // ── STAR operations (serialize whole AZOAResult<ISTARODK>) ──
            [QuestNodeType.StarGenerate] = WrappedObject(),
            [QuestNodeType.StarDeploy] = WrappedObject(),

            // ── Search (serialize whole AZOAResult<SearchResult>) ──
            [QuestNodeType.Search] = WrappedObject(),

            // ── Avatar NFT (serialize whole AZOAResult<AvatarNFTCompositeResult>) ──
            [QuestNodeType.AvatarNFTGetComposite] = WrappedObject(),

            // ── Blockchain (serialize whole AZOAResult<IBlockchainOperation>) ──
            // The nested Result.Parameters bag is provider-dependent (open), but the
            // top-level wrapper shape is known.
            [QuestNodeType.BlockchainExecute] = WrappedObject(),

            // ── Internal / control-flow ──
            // Condition: passes context.Node.Config through verbatim (arbitrary config JSON).
            [QuestNodeType.Condition] = NodeOutputShape.OpenShape,
            // ComposeOutputs: a { <upstreamNodeName>: <outputJsonString> } map — dynamic keys.
            [QuestNodeType.ComposeOutputs] = NodeOutputShape.OpenShape,

            // ── Holon-transformation nodes (economic-primitive-nodes) ──
            // GateCheck: fixed literal {"pass":true} on success.
            [QuestNodeType.GateCheck] = NodeOutputShape.Of(("pass", OutputFieldType.Boolean)),
            // Emit: serializes the tenant-shaped cfg.Payload directly (free-form).
            [QuestNodeType.Emit] = NodeOutputShape.OpenShape,

            // Tier-2 economic nodes serialize the WHOLE AZOAResult<T>.
            // Result = SwapQuoteResponse
            [QuestNodeType.Swap] = WrappedObject(),
            // Result = IBlockchainOperation (Grant mint / Transfer / Refund reverse-transfer)
            [QuestNodeType.Grant] = WrappedObject(),
            [QuestNodeType.Transfer] = WrappedObject(),
            [QuestNodeType.Refund] = WrappedObject(),
            // Result = FungibleTokenResult
            [QuestNodeType.FungibleTokenCreate] = WrappedObject(),

            // ── Fractionalization rails — serialize r.Result DIRECTLY (unwrapped) ──
            // Both write the flat BridgeTransactionResult shape (NOT the AZOAResult wrapper).
            [QuestNodeType.Bridge] = BridgeTransactionShape,
            [QuestNodeType.Back] = BridgeTransactionShape,
        };

    /// <summary>
    /// The flat <c>BridgeTransactionResult</c> shape — Bridge/Back serialize
    /// <c>r.Result</c> directly, so this is the top-level output object (no wrapper).
    /// </summary>
    private static NodeOutputShape BridgeTransactionShape => NodeOutputShape.Of(
        ("Id", OutputFieldType.String),
        ("AvatarId", OutputFieldType.String),           // Guid → JSON string
        ("SourceChain", OutputFieldType.String),
        ("TargetChain", OutputFieldType.String),
        ("SourceTokenId", OutputFieldType.String),
        ("TargetTokenId", OutputFieldType.String),
        ("SourceAddress", OutputFieldType.String),
        ("TargetAddress", OutputFieldType.String),
        ("Amount", OutputFieldType.Number),
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
        ("IdempotencyKey", OutputFieldType.String),
        ("Network", OutputFieldType.Number));           // ChainNetwork enum → int

    /// <summary>
    /// The declared output shape for <paramref name="nodeType"/>. Throws
    /// <see cref="NotSupportedException"/> for any unmapped type so a newly-added
    /// node type cannot silently skip its schema (mirrors
    /// <see cref="QuestNodeConfigRegistry.GetConfigType"/>).
    /// </summary>
    public static NodeOutputShape GetShape(QuestNodeType nodeType)
    {
        if (!_map.TryGetValue(nodeType, out var shape))
            throw new NotSupportedException(
                $"QuestNodeType.{nodeType} has no output-schema entry. Add it to QuestNodeOutputSchema.");
        return shape;
    }
}
