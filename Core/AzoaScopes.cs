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
    /// <summary>May create/list child users and issue child credentials.</summary>
    public const string TenantProvision = "tenant:provision";

    /// <summary>A child credential may create/manage wallets for its user.</summary>
    public const string WalletManage = "wallet:manage";

    /// <summary>May read the user's minimal authoritative KYC status projection.</summary>
    public const string KycRead = "kyc:read";

    /// <summary>May begin and submit KYC verification for a tenant-correlated user.</summary>
    public const string KycSubmit = "kyc:submit";

    /// <summary>
    /// security-review HIGH-2: the operator/admin capability that gates the
    /// destructive cross-user admin surfaces (key rotation, data backfill). It is
    /// deliberately NOT in the API-key-issuable scope vocabulary a tenant/child key
    /// can carry (see <see cref="IsApiKeyIssuableScope"/>): the two admin endpoints
    /// require a JWT-authenticated identity that carries this claim, so an X-Api-Key
    /// principal — which can only ever emit <c>scope</c> claims from its stored CSV —
    /// can never self-assert operator authority even if it stuffed the literal string
    /// into its scopes. Minted only for real admins by the JWT issuer.
    /// </summary>
    public const string Operator = "operator:admin";

    /// <summary>May govern node-local economic parameters; JWT-only, never API-key issuable.</summary>
    public const string NodeGovern = "node:govern";

    /// <summary>A child credential may mint/transfer NFTs for its user.</summary>
    public const string NftMint = "nft:mint";

    /// <summary>
    /// Coarse dApp-developer capability: an API key carrying this scope may perform
    /// the authoring/edit surfaces across the owner's holons, actions and dapp-series
    /// (the write actions gated by the <c>DappDevelop</c> authorization policy).
    /// Ownership is still enforced by each manager; this scope only decides whether a
    /// scoped key is permitted to reach the write paths at all.
    /// </summary>
    public const string DappDevelop = "dapp:develop";

    /// <summary>
    /// DApp lifecycle-management capability. Managers can compose, generate,
    /// deploy, and later grant DApp-local memberships without becoming operators.
    /// </summary>
    public const string DappManage = "dapp:manage";

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

    /// <summary>
    /// D19: may revoke a soulbound credential — an on-chain ASA clawback signed by the
    /// asset's clawback authority. Held by the credential's ISSUER, never its holder;
    /// the issuer check itself lives in <c>CredentialManager</c>, and this scope only
    /// decides whether a scoped credential may reach the revoke path at all.
    /// </summary>
    public const string CredentialRevokeSign = "credential:revoke:sign";

    // ── tenant-consent-delegation: non-value participation scopes ──────────────

    /// <summary>Tenant may execute actions for the user (non-value; safe in a
    /// Participation standing grant).</summary>
    public const string ActionExecute = "action:execute";

    /// <summary>
    /// D47 issuance (OI-23): a tenant may have units CREDITED into this person's account
    /// out of the platform reserve. It is INBOUND — the draw comes from the node's own
    /// reserve and is signed with the PLATFORM key, so it spends nothing the person holds.
    /// That is why it is deliberately NOT a member of <see cref="ValueSigningScopes"/> and
    /// why it sits in <see cref="ParticipationInboundCreditFloor"/>: a custodially
    /// onboarded person holds no AZOA credential and can never click "allow", so if being
    /// paid required an outbound value-SPENDING scope they could never be paid at all.
    /// <see cref="TransferSign"/> keeps meaning what it means — money leaving an account.
    /// </summary>
    public const string UnitsIssue = "units:issue";

    /// <summary>
    /// The value-signing scopes a tenant must NOT obtain via a no-UX Participation
    /// grant (H4) — each requires a deliberate <c>UserExplicit</c> grant. A grant
    /// request that carries any of these under <c>Participation</c> origin is rejected.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> ValueSigningScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            SwapSign, TransferSign, GrantSign, TokenCreateSign,
            // A revoke moves a real asset off a user's account with the platform's
            // clawback key. Like TokenCreateSign it is platform-SIGNED but may be
            // tenant-DRIVEN, so it must never arrive through a no-UX Participation grant.
            CredentialRevokeSign,
        };

    /// <summary>
    /// OI-23: the inbound-credit scopes EVERY <c>GrantOrigin.Participation</c> grant carries,
    /// stated as a positive signal rather than inferred from absence in
    /// <see cref="ValueSigningScopes"/>. <c>ConsentManager.GrantParticipationAsync</c> unions
    /// this set into the scopes of every participation grant it WRITES, which is the point at
    /// which the grant row exists — so the tenant-provisioning issuer, the join flow and any
    /// later participation caller all inherit it without each having to remember to list it.
    ///
    /// <para>It carries <see cref="UnitsIssue"/> and nothing else. <see cref="NftMint"/> is
    /// also inbound, but the operation-scope map below lets it sign a <c>Burn</c> as well,
    /// so handing it to every participation grant would widen more than a credit; the
    /// tenant-provisioned implied grant names it explicitly instead.</para>
    ///
    /// <para>The H4 exclusion in <c>ConsentManager.GrantAsync</c> runs over the UNION, so a
    /// value-signing scope smuggled in here would refuse every participation grant outright
    /// rather than quietly widening one — fail-closed by construction, not by review.</para>
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> ParticipationInboundCreditFloor =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            UnitsIssue,
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
    /// The operation type an <c>AllocationKind.IssueUnits</c> draw declares. Its OWN key,
    /// never <c>Transfer</c>: both move units, but a transfer spends the signer's holdings
    /// while an issuance credits the recipient out of the reserve. Sharing the key would let
    /// <see cref="UnitsIssue"/> sign an ordinary outbound transfer — exactly the mislabelling
    /// this map exists to refuse.
    /// </summary>
    public const string IssueUnitsOperationType = "IssueUnits";

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
            // An issuance is a reserve-funded CREDIT, so it signs under units:issue only —
            // and units:issue is absent from every other entry, so it can sign nothing else.
            [IssueUnitsOperationType]  = Set(UnitsIssue),
            ["Swap"]                  = Set(SwapSign),
            ["Exchange"]              = Set(SwapSign),
            ["fungible_token_create"] = Set(TokenCreateSign),
            // D19 soulbound revocation. Deliberately its OWN scope rather than reusing
            // TransferSign: a clawback moves an asset the signer does not hold, so a
            // user who consented to "sign my transfers" must not thereby consent to
            // having a credential taken from them.
            ["CredentialRevoke"]      = Set(CredentialRevokeSign),
        };

    private static System.Collections.Generic.IReadOnlySet<string> Set(params string[] scopes)
        => new System.Collections.Generic.HashSet<string>(scopes, StringComparer.Ordinal);

    /// <summary>
    /// security-review HIGH-2 defense-in-depth: scopes an API key must NEVER be able to
    /// emit as a claim, regardless of what its stored <c>ApiKey.Scopes</c> CSV contains.
    /// The <see cref="Operator"/> capability is the only such scope today — it authorizes
    /// destructive cross-user admin operations and must originate ONLY from a real
    /// admin's JWT. <c>ApiKeyAuthenticationHandler</c> filters these out at claim-emit
    /// time so a forged/misconfigured key CSV can never satisfy the <c>Operator</c> policy.
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlySet<string> ApiKeyForbiddenScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            Operator,
            NodeGovern,
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
    /// self-issuance allow-list (<see cref="IsIssuableByUser"/>); it names only the
    /// capability-style scopes as opposed to the fine-grained value/participation ones.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> IssuableCapabilityScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            DappDevelop,
            DappManage,
        };

    /// <summary>
    /// The scope vocabulary that can appear on self-issued API keys before caller-role
    /// filtering. <c>ApiKeyController.Create</c> applies the current-principal check.
    ///
    /// Membership rules, deliberately explicit rather than by-omission:
    ///   • <see cref="DappDevelop"/> and <see cref="DappManage"/> — role-bound dApp capabilities.
    ///   • The value/participation signing scopes that are ALREADY intended to be
    ///     key-carried by a tenant/child credential: wallet:manage, nft:mint,
    ///     action:execute, units:issue, swap:sign, transfer:sign, grant:sign,
    ///     token:create:sign, credential:revoke:sign.
    ///   • EXCLUDES <see cref="Operator"/> — admin authority never originates from a key
    ///     (also stripped at claim-emit time; see <see cref="ApiKeyForbiddenScopes"/>).
    ///   • EXCLUDES <see cref="TenantProvision"/> — closing a prior gap where it was
    ///     self-issuable purely by omission. Tenant provisioning must be granted by the
    ///     platform, not self-asserted on a plain user's key.
    /// Any scope NOT in this set is rejected at issuance.
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlySet<string> SelfIssuableScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            DappDevelop,
            DappManage,
            WalletManage,
            KycRead,
            KycSubmit,
            NftMint,
            ActionExecute,
            UnitsIssue,
            SwapSign,
            TransferSign,
            GrantSign,
            TokenCreateSign,
            CredentialRevokeSign,
        };

    private static readonly System.Collections.Generic.IReadOnlySet<string> RoleBoundDappScopes =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            DappDevelop,
            DappManage,
        };

    /// <summary>
    /// True iff <paramref name="scope"/> is part of the self-issued API-key vocabulary.
    /// Caller-specific role filtering is applied by <c>CanSelfIssueApiKeyScope</c>.
    /// </summary>
    public static bool IsIssuableByUser(string? scope)
        => !string.IsNullOrWhiteSpace(scope) && SelfIssuableScopes.Contains(scope);

    public static bool IsRoleBoundDappScope(string? scope)
        => !string.IsNullOrWhiteSpace(scope) && RoleBoundDappScopes.Contains(scope);

    /// <summary>Human-readable one-liner per self-issuable scope, for the key-issuance discovery surface.</summary>
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> ScopeDescriptions =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DappDevelop]     = "Author/edit holons, actions, and dApp-series (the dApp-developer write surface).",
            [DappManage]      = "Manage DApp lifecycle actions such as compose, generate, deploy, and membership.",
            [WalletManage]    = "Create and manage wallets.",
            [KycRead]         = "Read a minimal authoritative KYC status.",
            [KycSubmit]       = "Begin and submit identity verification.",
            [NftMint]         = "Mint, transfer, and burn NFTs.",
            [ActionExecute]    = "Execute actions (non-value participation).",
            [UnitsIssue]      = "Have units credited into an account from the platform reserve.",
            [SwapSign]        = "Sign a token swap with the user's key.",
            [TransferSign]    = "Sign a value transfer with the user's key.",
            [GrantSign]       = "Sign a grant/mint-to-actor.",
            [TokenCreateSign] = "Sign a fungible-token (ASA) create.",
            [CredentialRevokeSign] = "Revoke a soulbound credential you issued (on-chain clawback).",
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
