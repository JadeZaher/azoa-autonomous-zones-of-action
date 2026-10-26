// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// Per-project custodial treasury accounts: address derivation, sign-as-treasury, balance
/// reads, the credited/spent/held ledger, and close. See
/// <c>Services/Reserve/IProjectTreasury.cs</c> for what a project treasury is and why the
/// project must receive value on chain rather than as a row in a tenant's database.
/// </summary>
public sealed class ProjectTreasuryService : IProjectTreasury
{
    /// <summary>How many applied movement keys the replay window keeps, oldest evicted first.</summary>
    public const int MovementReplayWindowSize = 128;

    /// <summary>Largest page an operator list call will return.</summary>
    public const int MaxListLimit = 500;

    /// <summary>Attempts a movement makes before giving up on a compare-and-swap race.</summary>
    private const int MaxCasAttempts = 4;

    private readonly IKeyCustodyService _custody;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IProjectTreasuryLedgerStore _ledgerStore;
    private readonly ReserveOptions _options;

    public ProjectTreasuryService(
        IKeyCustodyService custody,
        IBlockchainProviderFactory providerFactory,
        IProjectTreasuryLedgerStore ledgerStore,
        ReserveOptions options)
    {
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // ── Account resolution ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryAccount>> ResolveAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default)
    {
        var derived = await DeriveAddressAsync(reference);
        if (derived.IsError || derived.Result is null)
            return AZOAResult<ProjectTreasuryAccount>.Failure(derived.Message);

        var address = derived.Result;
        var recordId = RecordIdFor(reference);

        var existing = await _ledgerStore.GetAsync(recordId, ct);
        if (existing.IsError)
            return AZOAResult<ProjectTreasuryAccount>.Failure(existing.Message);

        if (existing.Result is not null)
            return AccountFrom(reference, existing.Result, address, "Project treasury account resolved.");

        var now = DateTimeOffset.UtcNow;
        var row = new ProjectTreasuryLedger
        {
            Id = recordId,
            Chain = reference.NormalizedChainType,
            Network = _providerFactory.DefaultNetwork.ToString(),
            TenantId = SurrealLink.ToLink("user", reference.TenantIdText)!,
            ProjectReference = reference.NormalizedProjectReference,
            TreasuryAddress = address,
            DerivationVersion = ProjectTreasuryKeyDerivation.Version,
            Status = ProjectTreasuryStatus.Open.ToString(),
            Assets = ProjectTreasuryAssetCodec.Encode(new Dictionary<string, ProjectTreasuryAssetCounters>()),
            RecentOperationKeys = Array.Empty<string>(),
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claim = await _ledgerStore.CreateIfAbsentAsync(row, ct);
        if (claim.IsError || claim.Result is null)
            return AZOAResult<ProjectTreasuryAccount>.Failure(claim.Message);

        // A losing racer gets the winner's row, and the winner's row is checked against the
        // freshly derived address exactly like any other read — the create path must not be
        // the one place where a rotated platform key slips through unverified.
        return AccountFrom(
            reference,
            claim.Result.Row,
            address,
            claim.Result.Created
                ? "Project treasury account created."
                : "Project treasury account already existed; returning it unchanged.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryAccount>> FindAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default)
    {
        var row = await GetLedgerAsync(reference, ct);
        if (row.IsError || row.Result is null)
            return AZOAResult<ProjectTreasuryAccount>.Failure(row.Message);

        var derived = await DeriveAddressAsync(reference);
        if (derived.IsError || derived.Result is null)
            return AZOAResult<ProjectTreasuryAccount>.Failure(derived.Message);

        return AccountFrom(reference, row.Result, derived.Result, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<string>> GetBalanceAsync(
        ProjectTreasuryReference reference, string? assetId = null, CancellationToken ct = default)
    {
        var account = await FindAsync(reference, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<string>.Failure(account.Message);

        try
        {
            var provider = _providerFactory.GetProvider(account.Result.ChainType, _providerFactory.DefaultNetwork);
            return await provider.GetBalanceAsync(account.Result.Address, assetId, ct);
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<string>.Failure(
                $"No provider is registered for chain '{account.Result.ChainType}', so this project treasury's " +
                "balance cannot be read.");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> SignAsTreasuryAsync<T>(
        ProjectTreasuryReference reference, SigningContext signingContext, Func<byte[], Task<T>> sign)
    {
        if (sign is null)
            return AZOAResult<T>.Failure("A signing callback is required to sign as a project treasury.");

        var guard = GuardReference(reference);
        if (guard is not null)
            return AZOAResult<T>.Failure(guard);

        // A treasury is a platform-authority account. A caller-supplied context may still
        // carry an acting tenant + scope so the custody seam runs the live consent check;
        // what it may NOT do is downgrade this to a per-user signature.
        var chain = reference.NormalizedChainType;
        var context = signingContext.ForChain(chain);
        if (!context.IsPlatform)
            return AZOAResult<T>.Failure(
                "Signing as a project treasury requires a platform signing context.");

        // Refuse to sign for an account this node has no record of, and refuse when the
        // account on record was derived by a different key than the one configured now:
        // signing under a rotated key would move value from an account that is not the one
        // holding this project's money.
        var account = await FindAsync(reference);
        if (account.IsError || account.Result is null)
            return AZOAResult<T>.Failure(account.Message);

        var outcome = await _custody.WithPlatformSigningKeyAsync<AZOAResult<T>>(
            chain,
            isPlatformContext: true,
            context,
            async platformKey =>
            {
                var treasuryKey = ProjectTreasuryKeyDerivation.Derive(chain, platformKey, reference);
                if (treasuryKey is null)
                    return AZOAResult<T>.Failure(
                        $"No project treasury key could be derived on chain '{chain}'. " +
                        ProjectTreasuryKeyDerivation.SupportedChainsMessage);

                try
                {
                    return AZOAResult<T>.Success(await sign(treasuryKey));
                }
                finally
                {
                    // The derived key is this method's own allocation, so zeroing it is this
                    // method's job — the custody seam only owns the platform key it handed in.
                    CryptographicOperations.ZeroMemory(treasuryKey);
                }
            });

        if (outcome.IsError || outcome.Result is null)
            return AZOAResult<T>.Failure(outcome.Message);

        return outcome.Result;
    }

    // ── Ledger ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryLedger>> GetLedgerAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default)
    {
        var guard = GuardReference(reference);
        if (guard is not null)
            return AZOAResult<ProjectTreasuryLedger>.Failure(guard);

        var row = await _ledgerStore.GetAsync(RecordIdFor(reference), ct);
        if (row.IsError)
            return AZOAResult<ProjectTreasuryLedger>.Failure(row.Message);
        if (row.Result is null)
            return AZOAResult<ProjectTreasuryLedger>.Failure(
                $"No treasury exists for project '{reference.NormalizedProjectReference}' on " +
                $"'{reference.NormalizedChainType}'. Resolve it before using it.");

        if (GuardRowOwnership(reference, row.Result) is { } notYours)
            return AZOAResult<ProjectTreasuryLedger>.Failure(notYours);

        return AZOAResult<ProjectTreasuryLedger>.Success(row.Result, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
        Guid tenantId, int limit = 100, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>.Failure(
                "A tenant is required to list project treasuries.");

        var rows = await _ledgerStore.ListByTenantAsync(tenantId.ToString("N"), ct);
        if (rows.IsError || rows.Result is null)
            return AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>.Failure(rows.Message);

        var capped = Math.Clamp(limit, 1, MaxListLimit);
        return AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>.Success(
            rows.Result.Take(capped).ToList(), "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryMovementOutcome>> RecordMovementAsync(
        ProjectTreasuryMovementRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(
                "A project treasury movement request is required.");
        if (string.IsNullOrWhiteSpace(request.OperationKey))
            return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(
                "A treasury movement needs an operation key; without one a retry would double-count the asset.");

        var assetId = ProjectTreasuryAssetCodec.Normalize(request.AssetId);
        if (string.IsNullOrEmpty(assetId))
            return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(
                "An asset id is required for a treasury movement.");

        var operationKey = request.OperationKey.Trim();

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(request.Reference, ct);
            if (current.IsError || current.Result is null)
                return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(current.Message);

            var row = current.Result;
            var assets = ProjectTreasuryAssetCodec.Decode(row.Assets, out var decodeError);
            if (assets is null)
                return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(decodeError);

            var window = row.RecentOperationKeys?.ToList() ?? new List<string>();

            // Replay guard: a retried movement inside the window returns the counters as
            // they already stand and moves nothing. Outside the window the key is unknown
            // and the movement is applied, which is why callers keep their own anchor too.
            if (window.Contains(operationKey, StringComparer.Ordinal))
            {
                var replayed = assets.TryGetValue(assetId, out var known)
                    ? known
                    : ProjectTreasuryAssetCounters.Empty;
                return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                    new ProjectTreasuryMovementOutcome(
                        assetId, request.Kind, request.Amount, replayed, row.Version, Replayed: true),
                    "Duplicate treasury movement: the original result is returned and nothing moved.");
            }

            // A closed treasury is settled and retired. Accepting a credit into it would
            // record value against an account nothing is going to spend from again.
            if (!IsOpen(row))
                return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(
                    $"Project '{row.ProjectReference}' has a CLOSED treasury on '{row.Chain}'; " +
                    "no further movement is accepted.");

            // An asset the treasury has never held starts at zero rather than failing:
            // the first credit is legitimately the moment an asset appears. Availability
            // is still zero, so a spend or a hold on an unknown asset is refused by the
            // arithmetic below, not by an auto-created balance.
            if (!assets.TryGetValue(assetId, out var counters))
                counters = ProjectTreasuryAssetCounters.Empty;

            if (!counters.TryApply(request.Kind, request.Amount, assetId, out var next, out var applyError))
                return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(applyError);

            assets[assetId] = next;
            window.Add(operationKey);
            while (window.Count > MovementReplayWindowSize)
                window.RemoveAt(0);

            var expectedVersion = row.Version;
            row.Assets = ProjectTreasuryAssetCodec.Encode(assets);
            row.RecentOperationKeys = window;
            row.Version = expectedVersion + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            var saved = await _ledgerStore.UpdateIfVersionAsync(row, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                    new ProjectTreasuryMovementOutcome(
                        assetId, request.Kind, request.Amount, next, saved.Result.Version, Replayed: false),
                    "Treasury movement applied.");

            if (attempt == MaxCasAttempts)
                return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(saved.Message);
        }

        return AZOAResult<ProjectTreasuryMovementOutcome>.Failure(
            "Treasury movement lost every compare-and-swap attempt.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryLedger>> CloseAsync(
        ProjectTreasuryCloseRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<ProjectTreasuryLedger>.Failure("A project treasury close request is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return AZOAResult<ProjectTreasuryLedger>.Failure(
                "A close reason is required; retiring a project's account is always attributable.");
        if (request.ClosedByUserId == Guid.Empty)
            return AZOAResult<ProjectTreasuryLedger>.Failure(
                "The operator closing a project treasury must be identified.");

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(request.Reference, ct);
            if (current.IsError || current.Result is null)
                return current;

            var row = current.Result;
            if (!IsOpen(row))
                return AZOAResult<ProjectTreasuryLedger>.Success(
                    row, "Project treasury was already closed; nothing changed.");

            var assets = ProjectTreasuryAssetCodec.Decode(row.Assets, out var decodeError);
            if (assets is null)
                return AZOAResult<ProjectTreasuryLedger>.Failure(decodeError);

            // Closing over live value would strand a project's money in an account no
            // movement is allowed to spend from any more. Sweep first, then close.
            foreach (var (unsettledAssetId, counters) in assets)
            {
                if (counters.IsSettled)
                    continue;

                return AZOAResult<ProjectTreasuryLedger>.Failure(
                    $"Asset '{unsettledAssetId}' still shows {counters.Available} available and " +
                    $"{counters.Held} held on this treasury. Settle it before closing, or the " +
                    "project's units would be stranded in an account nothing can spend from.");
            }

            var expectedVersion = row.Version;
            row.Status = ProjectTreasuryStatus.Closed.ToString();
            row.ClosedAt = DateTimeOffset.UtcNow;
            row.ClosedByUserId = SurrealLink.ToLink("user", request.ClosedByUserId.ToString("N"));
            row.CloseReason = request.Reason.Trim();
            row.Version = expectedVersion + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            var saved = await _ledgerStore.UpdateIfVersionAsync(row, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<ProjectTreasuryLedger>.Success(saved.Result, "Project treasury closed.");

            if (attempt == MaxCasAttempts)
                return AZOAResult<ProjectTreasuryLedger>.Failure(saved.Message);
        }

        return AZOAResult<ProjectTreasuryLedger>.Failure(
            "Project treasury close lost every compare-and-swap attempt.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Null when the reference can address an account; the refusal message otherwise.</summary>
    private string? GuardReference(ProjectTreasuryReference reference)
    {
        if (reference is null || !reference.IsComplete)
            return "A project treasury needs a chain, a tenant and a project reference.";

        var chain = reference.NormalizedChainType;
        if (!IsChainApproved(chain))
            return
                $"Chain '{chain}' is not listed under {ReserveOptions.SectionName}:Chains, so this node holds no " +
                "project treasuries there. Add it deliberately before any value moves on that chain.";

        if (!ProjectTreasuryKeyDerivation.IsSupported(chain))
            return
                $"No project treasury key derivation is registered for chain '{chain}'. " +
                ProjectTreasuryKeyDerivation.SupportedChainsMessage;

        return null;
    }

    /// <summary>
    /// The project's treasury address, derived inside the custody door. Like the reserve's
    /// own address it is a by-product of proving the node can sign for the account, so a
    /// chain whose platform key is missing yields NO address rather than an unusable one.
    /// </summary>
    private async Task<AZOAResult<string>> DeriveAddressAsync(ProjectTreasuryReference reference)
    {
        var guard = GuardReference(reference);
        if (guard is not null)
            return AZOAResult<string>.Failure(guard);

        var chain = reference.NormalizedChainType;
        var derived = await _custody.WithPlatformSigningKeyAsync(
            chain,
            isPlatformContext: true,
            SigningContext.PlatformFor(chain),
            key => Task.FromResult(ProjectTreasuryKeyDerivation.DeriveAddress(chain, key, reference)));

        if (derived.IsError)
            return AZOAResult<string>.Failure(
                $"No project treasury account is available on '{chain}': {derived.Message}");

        if (string.IsNullOrWhiteSpace(derived.Result))
            return AZOAResult<string>.Failure(
                "No treasury address could be derived for project " +
                $"'{reference.NormalizedProjectReference}' on chain '{chain}'. " +
                ProjectTreasuryKeyDerivation.SupportedChainsMessage);

        return AZOAResult<string>.Success(derived.Result!, "Success");
    }

    /// <summary>
    /// Builds the account view from a persisted row, refusing when the row belongs to
    /// another tenant, or when the address on record disagrees with what the configured
    /// key derives today. The latter means the platform key changed under the node, and
    /// every later call would address an account holding none of this project's value.
    /// </summary>
    private static AZOAResult<ProjectTreasuryAccount> AccountFrom(
        ProjectTreasuryReference reference, ProjectTreasuryLedger row, string derivedAddress, string message)
    {
        if (GuardRowOwnership(reference, row) is { } notYours)
            return AZOAResult<ProjectTreasuryAccount>.Failure(notYours);

        if (!string.Equals(row.DerivationVersion, ProjectTreasuryKeyDerivation.Version, StringComparison.Ordinal))
            return AZOAResult<ProjectTreasuryAccount>.Failure(
                $"Project '{row.ProjectReference}' has a treasury derived by rule " +
                $"'{row.DerivationVersion}', but this node derives '{ProjectTreasuryKeyDerivation.Version}'. " +
                "Refusing every treasury operation on it until the rules agree.");

        if (!string.Equals(row.TreasuryAddress, derivedAddress, StringComparison.Ordinal))
            return AZOAResult<ProjectTreasuryAccount>.Failure(
                $"The treasury address derived for project '{row.ProjectReference}' on '{row.Chain}' does not " +
                "match the address frozen on its ledger row. The platform key this node signs with has changed, " +
                "so the derived account is not the account holding this project's value. Refusing every " +
                "treasury operation on it until the configured key and the recorded address agree.");

        return AZOAResult<ProjectTreasuryAccount>.Success(
            new ProjectTreasuryAccount(
                row.Chain,
                ParseTenant(row.TenantId),
                row.ProjectReference,
                row.TreasuryAddress,
                row.DerivationVersion,
                ParseStatus(row.Status)),
            message);
    }

    /// <summary>
    /// Null when <paramref name="row"/> is the treasury <paramref name="reference"/> names;
    /// the refusal otherwise. Every read reaches a row through a tenant-scoped record id, so
    /// a row whose recorded tenant disagrees was written under one tenant's key carrying
    /// another tenant's id — exactly the shape that would let the tenant-scoped resolve route
    /// (<c>Controllers/ProjectTreasuryTenantController.cs</c>) hand back somebody else's
    /// account. Enforced HERE, at the read, so admin and tenant doors alike inherit it, and
    /// deliberately says nothing about whose it actually is.
    /// </summary>
    private static string? GuardRowOwnership(ProjectTreasuryReference reference, ProjectTreasuryLedger row)
    {
        if (ParseTenant(row.TenantId) == reference.TenantId && reference.TenantId != Guid.Empty)
            return null;

        return
            $"The treasury on record for project '{reference.NormalizedProjectReference}' on " +
            $"'{reference.NormalizedChainType}' is not this tenant's. Refusing every operation on it.";
    }

    private string RecordIdFor(ProjectTreasuryReference reference) =>
        ProjectTreasuryLedger.RecordIdFor(
            reference.NormalizedChainType,
            _providerFactory.DefaultNetwork.ToString(),
            reference.TenantIdText,
            reference.NormalizedProjectReference);

    /// <summary>The third door onto the same counter: read the effective set, never <c>Chains</c> raw (OI-74).</summary>
    private bool IsChainApproved(string chain) => _options.PermitsChain(chain);

    /// <summary>An unreadable status is treated as CLOSED, never as open.</summary>
    private static bool IsOpen(ProjectTreasuryLedger row) =>
        ParseStatus(row.Status) == ProjectTreasuryStatus.Open;

    private static ProjectTreasuryStatus ParseStatus(string? status) =>
        Enum.TryParse<ProjectTreasuryStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : ProjectTreasuryStatus.Closed;

    /// <summary>
    /// Tenant id off a row. <c>tenant_id</c> is declared <c>record&lt;user&gt;</c>
    /// (<c>Generated/Schemas/project_treasury_ledger.surql:30</c>), so a read can come back
    /// as <c>user:abc</c> and, when the id needs escaping, as <c>user:⟨abc⟩</c>. Both are
    /// unwrapped, and both the dashless and dashed Guid forms are accepted: this value now
    /// decides a REFUSAL (see <see cref="GuardRowOwnership"/>), so a format this parser
    /// merely failed to recognise must not read as "belongs to somebody else".
    /// </summary>
    private static Guid ParseTenant(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return Guid.Empty;

        var text = tenantId.Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && colon < text.Length - 1)
            text = text[(colon + 1)..];

        text = text.Trim('⟨', '⟩', '`');

        return Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
    }
}

/// <summary>
/// Turns the chain's platform key into ONE project's treasury key. The node stores no
/// per-project secret: the key is an HKDF-SHA256 child of the platform key, bound to the
/// chain, the tenant and the project reference, so two projects can never resolve to one
/// account and a project's account is reproducible from configuration alone.
/// <para>
/// ⚠ The derivation's parent is the platform key, so ROTATING that key moves every
/// project treasury address. The ledger freezes the address it derived and
/// <c>ProjectTreasuryService</c> refuses to resolve or sign when the two disagree, which
/// makes a rotation a loud failure rather than a silent one.
/// </para>
/// <para>
/// Chains are opt-in one rule at a time. Algorand's signing key IS a 32-byte Ed25519
/// seed, so an HKDF output is directly a valid key and the address comes out of the same
/// derivation the platform reserve uses. Solana's signing key is a 64-byte
/// seed ‖ public-key pair and Ethereum's is a range-checked secp256k1 scalar; neither is
/// an HKDF output on its own, so both fail closed here NAMING the chain rather than
/// producing a key some signer would reject or, worse, accept as another account.
/// </para>
/// </summary>
public static class ProjectTreasuryKeyDerivation
{
    /// <summary>Derivation rule id, frozen on every ledger row it produced.</summary>
    public const string Version = "azoa/project-treasury/v1";

    /// <summary>Chains this derivation supports, for a caller-safe error message.</summary>
    public static string SupportedChainsMessage =>
        "Project treasury keys can be derived for: Algorand, Simulated.";

    /// <summary>Domain-separating HKDF salt; changing it changes every derived address.</summary>
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes(Version);

    /// <summary>Raw signing-key length per chain, mirroring that chain's platform key shape.</summary>
    private static readonly IReadOnlyDictionary<string, int> KeyLengthByChain =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Algorand"] = AlgorandPlatformKeyDerivation.PrivateKeyLength,
            // Any 32 bytes are a valid simulated key; see Services/Reserve/AGENTS.md §simulated-project-treasury.
            [WalletKeyService.SimulatedChainType] = SimulatedPlatformKeyDerivation.PrivateKeyLength,
        };

    /// <summary>True when a treasury key can be derived on this chain at all.</summary>
    public static bool IsSupported(string? chainType) =>
        !string.IsNullOrWhiteSpace(chainType) && KeyLengthByChain.ContainsKey(chainType.Trim());

    /// <summary>
    /// Derives one project's raw treasury signing key, or null when the chain has no rule
    /// or the platform key is unusable. The caller owns the buffer and MUST zero it.
    /// </summary>
    public static byte[]? Derive(string chainType, byte[] platformKey, ProjectTreasuryReference reference)
    {
        if (platformKey is null || platformKey.Length == 0)
            return null;
        if (reference is null || !reference.IsComplete)
            return null;
        if (string.IsNullOrWhiteSpace(chainType) ||
            !KeyLengthByChain.TryGetValue(chainType.Trim(), out var keyLength))
        {
            return null;
        }

        var info = Encoding.UTF8.GetBytes(CanonicalInfo(chainType, reference));

        // HKDF-SHA256 rather than a bare hash: the extract step gives the child key a
        // uniform distribution even when the platform key is not, and the info string
        // is what makes two projects' children independent.
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, platformKey, keyLength, Salt, info);
    }

    /// <summary>
    /// Derives the project's treasury ADDRESS. It runs inside the custody door and reuses
    /// <see cref="PlatformReserveAddressDerivation"/> deliberately: a treasury address and
    /// a reserve address must come out of ONE rule, or the node would hold value at an
    /// address it computed two different ways.
    /// </summary>
    public static string? DeriveAddress(string chainType, byte[] platformKey, ProjectTreasuryReference reference)
    {
        var treasuryKey = Derive(chainType, platformKey, reference);
        if (treasuryKey is null)
            return null;

        try
        {
            return PlatformReserveAddressDerivation.Derive(chainType, treasuryKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(treasuryKey);
        }
    }

    /// <summary>The HKDF info string: chain, tenant and project, canonicalised in one place.</summary>
    private static string CanonicalInfo(string chainType, ProjectTreasuryReference reference) =>
        $"{chainType.Trim().ToLowerInvariant()}|{reference.TenantIdText}|{reference.NormalizedProjectReference}";
}

/// <summary>
/// Encodes and decodes the ledger's <c>assets</c> object. Amounts round-trip as exact
/// decimal strings so a balance wider than i64 is never truncated. Asset ids are TRIMMED
/// ONLY and never case-folded: an Algorand asset id is numeric but a Solana mint is
/// base58, where <c>A</c> and <c>a</c> are different accounts.
/// </summary>
public static class ProjectTreasuryAssetCodec
{
    private const string CreditedKey = "credited";
    private const string SpentKey = "spent";
    private const string HeldKey = "held";

    /// <summary>Canonical asset key: trimmed, case preserved.</summary>
    public static string Normalize(string? assetId) => assetId?.Trim() ?? string.Empty;

    /// <summary>Serialises the asset map into the flexible object the row persists.</summary>
    public static JsonElement Encode(IReadOnlyDictionary<string, ProjectTreasuryAssetCounters> assets)
    {
        var wire = assets.ToDictionary(
            entry => entry.Key,
            entry => new Dictionary<string, string>
            {
                [CreditedKey] = entry.Value.Credited.ToString(CultureInfo.InvariantCulture),
                [SpentKey] = entry.Value.Spent.ToString(CultureInfo.InvariantCulture),
                [HeldKey] = entry.Value.Held.ToString(CultureInfo.InvariantCulture),
            },
            StringComparer.Ordinal);

        return JsonSerializer.SerializeToElement(wire);
    }

    /// <summary>
    /// Reads the persisted asset map. A malformed blob returns null with an error rather
    /// than an empty map: treating unreadable counters as "nothing committed" would hand a
    /// caller availability the project does not have.
    /// </summary>
    public static Dictionary<string, ProjectTreasuryAssetCounters>? Decode(
        JsonElement? assets, out string error)
    {
        error = string.Empty;
        var result = new Dictionary<string, ProjectTreasuryAssetCounters>(StringComparer.Ordinal);

        if (assets is not { } element || element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "The project treasury ledger's assets field is not an object; refusing to read it as empty.";
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                error = $"Asset '{property.Name}' is malformed on the project treasury ledger row.";
                return null;
            }

            if (!TryReadAmount(property.Value, CreditedKey, out var credited) ||
                !TryReadAmount(property.Value, SpentKey, out var spent) ||
                !TryReadAmount(property.Value, HeldKey, out var held))
            {
                error = $"Asset '{property.Name}' carries an unreadable counter on the project treasury ledger row.";
                return null;
            }

            result[Normalize(property.Name)] = new ProjectTreasuryAssetCounters(credited, spent, held);
        }

        return result;
    }

    private static bool TryReadAmount(JsonElement asset, string key, out BigInteger amount)
    {
        amount = BigInteger.Zero;
        if (!asset.TryGetProperty(key, out var value))
            return true;

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        return raw is not null &&
               BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}

/// <summary>
/// SurrealDB-backed project treasury ledger. Colocated with the service because
/// <c>Providers/Stores/Surreal/</c> is outside this change's file boundary; the seam is
/// <see cref="IProjectTreasuryLedgerStore"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealProjectTreasuryLedgerStore : IProjectTreasuryLedgerStore
{
    private const string Table = ProjectTreasuryLedger.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealProjectTreasuryLedgerStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryLedger?>> GetAsync(
        string recordId, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery<ProjectTreasuryLedger>.Key(recordId);
            var row = await _executor.QuerySingleAsync<ProjectTreasuryLedger>(query, ct);
            return new AZOAResult<ProjectTreasuryLedger?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ProjectTreasuryLedger?>()
                .CaptureException(ex, $"Project treasury ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryLedgerClaim>> CreateIfAbsentAsync(
        ProjectTreasuryLedger row, CancellationToken ct = default)
    {
        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var created = response.GetValues<ProjectTreasuryLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<ProjectTreasuryLedgerClaim>.Success(
                new ProjectTreasuryLedgerClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the idempotent case, not a fault —
            // but only when the row really is there now. Re-read before concluding that,
            // so a transport failure is never reported as "already resolved".
            var existing = await GetAsync(row.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<ProjectTreasuryLedgerClaim>.Success(
                    new ProjectTreasuryLedgerClaim(existing.Result, Created: false), "Already resolved.");

            return new AZOAResult<ProjectTreasuryLedgerClaim>()
                .CaptureException(ex, $"Project treasury ledger create failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ProjectTreasuryLedger>> UpdateIfVersionAsync(
        ProjectTreasuryLedger row, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            // Single-winner conditional update (the ActionRun precedent): the write lands
            // only while the persisted version is still the one this caller reasoned over,
            // so two concurrent spends can never both consume the same availability.
            var update = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) CONTENT $_body WHERE version = $_expected RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row)
                .WithParam("_expected", expectedVersion);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(update, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            if (response.Count == 0 || !response[0].IsOk || response[0].AffectedCount() != 1)
                return AZOAResult<ProjectTreasuryLedger>.Failure(
                    $"Project treasury ledger row '{row.Id}' changed underneath this update " +
                    $"(expected version {expectedVersion}).");

            var persisted = response.GetValues<ProjectTreasuryLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<ProjectTreasuryLedger>.Success(persisted, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<ProjectTreasuryLedger>()
                .CaptureException(ex, $"Project treasury ledger update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
        string tenantId, CancellationToken ct = default)
    {
        try
        {
            // tenant_id is a record<user> column, so the parameter has to be bound as a
            // native record id — a bare string compares equal to nothing and would return
            // an empty list with no error at all.
            var query = SurrealQuery
                .Of("SELECT * FROM project_treasury_ledger WHERE tenant_id = $_tenant ORDER BY created_at DESC")
                .WithParam("_tenant", SurrealRecordParam.Of("user", tenantId));

            var rows = await _executor.QueryAsync<ProjectTreasuryLedger>(query, ct);
            return AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>.Success(
                rows?.ToList() ?? new List<ProjectTreasuryLedger>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>()
                .CaptureException(ex, $"Project treasury ledger list failed: {ex.Message}");
        }
    }
}
