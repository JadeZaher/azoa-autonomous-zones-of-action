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

    /// <summary>A child credential may mint/transfer NFTs for its avatar.</summary>
    public const string NftMint = "nft:mint";

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
