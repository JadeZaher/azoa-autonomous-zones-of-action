using System.Linq;

namespace AZOA.WebAPI.Core;

/// <summary>
/// Central v1 scope vocabulary layered on the existing CSV <c>ApiKey.Scopes</c>
/// field (<c>Models/ApiKey.cs:29</c>) — no schema change to scope storage, only a
/// defined vocabulary and a single enforcement point
/// (<see cref="ClaimsPrincipalExtensions.HasScope"/> + the <c>TenantScope</c>
/// authorization policy).
///
/// IMPORTANT: an empty/NONE <c>Scopes</c> CSV still means "full access" for
/// <em>non-tenant legacy</em> keys (<c>Models/ApiKey.cs:26-28</c>), but a tenant
/// key MUST carry <see cref="TenantProvision"/> explicitly — "empty = full" does
/// NOT silently grant tenant powers (the policy checks for the literal scope).
/// </summary>
public static class AzoaScopes
{
    /// <summary>May create/list child avatars and issue child credentials.</summary>
    public const string TenantProvision = "tenant:provision";

    /// <summary>A child credential may create/manage wallets for its avatar.</summary>
    public const string WalletManage = "wallet:manage";

    /// <summary>
    /// security-review HIGH-2: the operator/admin capability that gates the
    /// destructive cross-avatar admin surfaces (key rotation, data backfill). It is
    /// deliberately NOT in the API-key-issuable scope vocabulary a tenant/child key
    /// can carry (see <see cref="IsApiKeyIssuableScope"/>): the two admin endpoints
    /// require a JWT-authenticated identity that carries this claim, so an X-Api-Key
    /// principal — which can only ever emit <c>scope</c> claims from its stored CSV —
    /// can never self-assert operator authority even if it stuffed the literal string
    /// into its scopes. Minted only for real admins by the JWT issuer.
    /// </summary>
    public const string Operator = "operator:admin";

    /// <summary>A child credential may mint/transfer NFTs for its avatar.</summary>
    public const string NftMint = "nft:mint";

    /// <summary>
    /// Coarse dApp-developer capability: an API key carrying this scope may perform
    /// the authoring/edit surfaces across the owner's holons, quests and dapp-series
    /// (the write actions gated by the <c>DappDevelop</c> authorization policy).
    /// Ownership is still enforced by each manager; this scope only decides whether a
    /// scoped key is permitted to reach the write paths at all.
    /// </summary>
    public const string DappDevelop = "dapp:develop";

    // ── tenant-consent-delegation: value-signing scopes (H4) ──────────────────
    // These authorize a tenant-driven action that DECRYPTS a user's signing key.
    // They are EXCLUDED from the no-UX Participation standing grant (a value action
    // requires a deliberate UserExplicit grant) and are the scopes the custody seam
    // checks a live ConsentGrant against before key decrypt (AC4).

    /// <summary>Tenant may drive a token swap that signs with the user's key.</summary>
    public const string SwapSign = "swap:sign";

    /// <summary>Tenant may drive a value transfer that signs with the user's key.</summary>
    public const string TransferSign = "transfer:sign";

    /// <summary>Tenant may drive a grant/mint-to-actor that signs (platform or user key).</summary>
    public const string GrantSign = "grant:sign";

    /// <summary>Tenant may drive a fungible-token (ASA) create that signs.</summary>
    public const string TokenCreateSign = "token:create:sign";

    // ── tenant-consent-delegation: non-value participation scopes ──────────────

    /// <summary>Tenant may execute quests for the user (non-value; safe in a
    /// Participation standing grant).</summary>
    public const string QuestExecute = "quest:execute";

    /// <summary>
    /// The value-signing scopes a tenant must NOT obtain via a no-UX Participation
    /// grant (H4) — each requires a deliberate <c>UserExplicit</c> grant. A grant
    /// request that carries any of these under <c>Participation</c> origin is rejected.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> ValueSigningScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            SwapSign, TransferSign, GrantSign, TokenCreateSign,
        };

    // ── security-review S6: scope ↔ operation-type consistency ─────────────────
    // The signing scope a tenant-driven op declares is a constant chosen by each
    // op-builder. If a builder MISLABELS a value-moving op (e.g. stamps a transfer
    // with nft:mint because it reused a Mint code path), the consent gate would check
    // the wrong capability — a user who granted only nft:mint could be made to sign a
    // transfer. This central map is the single source of truth for which scope(s) a
    // given operation type may legitimately sign under; the dispatch seam asserts the
    // declared scope is in the allowed set BEFORE the gate runs (fail-closed: a
    // mismatch is a programming error and must never reach a key decrypt).

    /// <summary>
    /// The scopes each blockchain operation type may legitimately sign under. Keyed by
    /// <c>IBlockchainOperation.OperationType</c> (ordinal). An operation type absent
    /// from this map has no permitted tenant-driven signing scope (deny).
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlySet<string>> OperationScopeMap =
        new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Mint"]                  = Set(NftMint, GrantSign),
            ["Burn"]                  = Set(NftMint),
            ["Transfer"]              = Set(TransferSign),
            ["Swap"]                  = Set(SwapSign),
            ["Exchange"]              = Set(SwapSign),
            ["fungible_token_create"] = Set(TokenCreateSign),
        };

    private static System.Collections.Generic.IReadOnlySet<string> Set(params string[] scopes)
        => new System.Collections.Generic.HashSet<string>(scopes, StringComparer.Ordinal);

    /// <summary>
    /// security-review HIGH-2 defense-in-depth: scopes an API key must NEVER be able to
    /// emit as a claim, regardless of what its stored <c>ApiKey.Scopes</c> CSV contains.
    /// The <see cref="Operator"/> capability is the only such scope today — it authorizes
    /// destructive cross-avatar admin operations and must originate ONLY from a real
    /// admin's JWT. <c>ApiKeyAuthenticationHandler</c> filters these out at claim-emit
    /// time so a forged/misconfigured key CSV can never satisfy the <c>Operator</c> policy.
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlySet<string> ApiKeyForbiddenScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            Operator,
        };

    /// <summary>
    /// True iff <paramref name="scope"/> may be emitted as a <c>scope</c> claim by an
    /// API-key principal. Returns false for admin-only capabilities (see
    /// <see cref="ApiKeyForbiddenScopes"/>) so the API-key auth handler can strip them.
    /// </summary>
    public static bool IsApiKeyIssuableScope(string? scope)
        => !string.IsNullOrWhiteSpace(scope) && !ApiKeyForbiddenScopes.Contains(scope);

    /// <summary>
    /// The coarse capability scopes a caller may deliberately attach to their OWN new
    /// API key (the dapp-developer surface). This is a strict subset of the broader
    /// self-issuance allow-list (<see cref="IsIssuableByAvatar"/>); it names only the
    /// capability-style scopes as opposed to the fine-grained value/participation ones.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> IssuableCapabilityScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            DappDevelop,
        };

    /// <summary>
    /// The complete allow-list of scopes an ordinary avatar may place on a NEW API key
    /// it creates for itself (issuance-time validation in <c>ApiKeyController.Create</c>).
    ///
    /// Membership rules, deliberately explicit rather than by-omission:
    ///   • <see cref="DappDevelop"/> — the coarse dApp-developer capability (this task).
    ///   • The value/participation signing scopes that are ALREADY intended to be
    ///     key-carried by a tenant/child credential: wallet:manage, nft:mint,
    ///     quest:execute, swap:sign, transfer:sign, grant:sign, token:create:sign.
    ///   • EXCLUDES <see cref="Operator"/> — admin authority never originates from a key
    ///     (also stripped at claim-emit time; see <see cref="ApiKeyForbiddenScopes"/>).
    ///   • EXCLUDES <see cref="TenantProvision"/> — closing a prior gap where it was
    ///     self-issuable purely by omission. Tenant provisioning must be granted by the
    ///     platform, not self-asserted on a plain avatar's key.
    /// Any scope NOT in this set is rejected at issuance.
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlySet<string> SelfIssuableScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            DappDevelop,
            WalletManage,
            NftMint,
            QuestExecute,
            SwapSign,
            TransferSign,
            GrantSign,
            TokenCreateSign,
        };

    /// <summary>
    /// True iff an ordinary avatar may attach <paramref name="scope"/> to a new API key
    /// it issues for itself. A null/blank scope is not issuable (callers treat the
    /// empty CSV — "full access" legacy — separately, before ever calling this).
    /// See <see cref="SelfIssuableScopes"/> for the allow-list and its rationale.
    /// </summary>
    public static bool IsIssuableByAvatar(string? scope)
        => !string.IsNullOrWhiteSpace(scope) && SelfIssuableScopes.Contains(scope);

    /// <summary>Human-readable one-liner per self-issuable scope, for the key-issuance discovery surface.</summary>
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> ScopeDescriptions =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DappDevelop]     = "Author/edit holons, quests, and dApp-series (the dApp-developer write surface).",
            [WalletManage]    = "Create and manage wallets.",
            [NftMint]         = "Mint, transfer, and burn NFTs.",
            [QuestExecute]    = "Execute quests (non-value participation).",
            [SwapSign]        = "Sign a token swap with the avatar's key.",
            [TransferSign]    = "Sign a value transfer with the avatar's key.",
            [GrantSign]       = "Sign a grant/mint-to-actor.",
            [TokenCreateSign] = "Sign a fungible-token (ASA) create.",
        };

    /// <summary>
    /// The self-issuable scopes paired with a human description — the source of truth for
    /// the <c>GET /api/apikey/scopes</c> discovery endpoint. Ordered by
    /// <see cref="SelfIssuableScopes"/> insertion so the UI renders them stably.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<(string Scope, string Description)> IssuableScopeCatalog()
        => SelfIssuableScopes
            .Select(s => (Scope: s, Description: ScopeDescriptions.TryGetValue(s, out var d) ? d : s))
            .ToList();

    /// <summary>
    /// S6 guardrail: true iff <paramref name="scope"/> is a legitimate signing scope
    /// for <paramref name="operationType"/>. A null/blank scope is never valid (a
    /// tenant-driven sign must name a concrete scope). An unknown operation type, or a
    /// scope not in that type's allowed set, returns false — the caller fails closed.
    /// </summary>
    public static bool IsScopeValidForOperation(string? operationType, string? scope)
    {
        if (string.IsNullOrWhiteSpace(operationType) || string.IsNullOrWhiteSpace(scope))
            return false;
        return OperationScopeMap.TryGetValue(operationType, out var allowed)
               && allowed.Contains(scope);
    }
}
