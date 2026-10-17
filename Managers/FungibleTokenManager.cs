using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class FungibleTokenManager(IValueAccessService valueAccess, IWalletManager wallets,
    IWalletStore walletStore, IBlockchainProviderFactory providers, IFungibleCreationStore operations,
    IFungibleCreationService lifecycle, IPlatformReserve reserve, ReserveOptions options, INodeGovernanceGuard governance,
    SigningAdmissionAuthorityFactory signingAuthority, IActionNodeExecutionStore executions)
    : IFungibleTokenManager
{
    /// <inheritdoc/>
    public Task<AZOAResult<FungibleTokenResult>> CreateAsync(Guid userId, FungibleTokenCreateRequest request,
        Guid callerUserId, string? clientIdempotencyKey, string apiKeyId, Guid? actingTenantId = null)
        => CreateCoreAsync(userId, request, callerUserId, clientIdempotencyKey, actingTenantId, null);

    /// <summary>Admits an operator-selected custody destination without accepting costs or signing.</summary>
    public Task<AZOAResult<FungibleTokenResult>> CreateAsync(Guid userId, FungibleTokenCreateRequest request,
        Guid callerUserId, string? clientIdempotencyKey, string apiKeyId, Guid? actingTenantId, FungibleMintTarget mintTarget)
        => CreateCoreAsync(userId, request, callerUserId, clientIdempotencyKey, actingTenantId, mintTarget);

    private async Task<AZOAResult<FungibleTokenResult>> CreateCoreAsync(Guid actor, FungibleTokenCreateRequest request,
        Guid caller, string? key, Guid? tenant, FungibleMintTarget? selectedTarget)
    {
        if (actor == Guid.Empty || actor != caller || request is null || string.IsNullOrWhiteSpace(key) || key.Length > 200
            || request.Total == 0 || request.Decimals is < 0 or > 19 || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.UnitName) || string.IsNullOrWhiteSpace(request.ChainType) || request.HolonId == Guid.Empty)
            return AZOAResult<FungibleTokenResult>.Failure("An authenticated owner, original request key and valid token descriptor are required.");
        var id = FungibleCreationIdentity.OperationId(actor, tenant, key);
        var requestHash = CanonicalJson.Hash(new { Request = request, SelectedTarget = selectedTarget, request.ActionAuthority });
        var prior = await operations.GetAsync(id);
        if (prior is not null)
        {
            var saved = JsonSerializer.Deserialize<FungibleCreationCommand>(prior.RequestJson)!;
            if (saved.ClientRequestHash != requestHash)
                return AZOAResult<FungibleTokenResult>.Failure("The original request key belongs to another token descriptor or authority.");
            return Project(await lifecycle.GetAsync(id, actor, tenant), actor, true);
        }
        if (request.ActionAuthority is null && (request.AuthenticationWitness is not { } admissionCredential
            || admissionCredential.ActorUserId != actor || admissionCredential.TenantId != tenant))
            return AZOAResult<FungibleTokenResult>.Failure("The original authenticated signing credential is required.");
        var policy = await governance.EnsureAllowedAsync(request.ChainType, NodeGovernanceAssetTypes.FungibleToken, "fungible-token:create");
        if (policy.IsRefused) return AZOAResult<FungibleTokenResult>.FailureWithCode(policy.ConsumerMessage, policy.Code!);
        var gate = await valueAccess.RequireValueAccessAsync(actor, tenant);
        if (gate.IsError) return AZOAResult<FungibleTokenResult>.FailureWithCode(gate.Message, gate.Code!);
        var network = request.Network ?? providers.DefaultNetwork;
        IBlockchainProvider provider;
        try { provider = providers.GetProvider(request.ChainType, network); }
        catch (BlockchainProviderNotFoundException) { return AZOAResult<FungibleTokenResult>.Failure("The requested fungible creation provider is unavailable."); }
        if (!provider.TryGetModule<IFungibleCreationModule>(out _))
            return AZOAResult<FungibleTokenResult>.Failure("The requested provider has no fungible creation capability.");
        var target = selectedTarget;
        if (target is null)
        {
            if (!Enum.TryParse<FungibleMintTarget>(options.FungibleMintTarget, true, out var configured) || !Enum.IsDefined(configured))
                return AZOAResult<FungibleTokenResult>.Failure("The configured fungible custody destination is invalid.");
            target = configured;
        }
        if (!Enum.IsDefined(target.Value)) return AZOAResult<FungibleTokenResult>.Failure("Invalid fungible custody destination.");
        var existing = await walletStore.GetByUserAsync(actor);
        if (existing.IsError) return AZOAResult<FungibleTokenResult>.Failure(existing.Message);
        var wallet = existing.Result?.Where(item => item.ChainType.Equals(provider.ChainType, StringComparison.OrdinalIgnoreCase)
            && item.WalletType == WalletType.Platform).OrderBy(item => item.Id).FirstOrDefault();
        var provisioned = wallet is null;
        if (wallet is null)
        {
            var created = await wallets.BootstrapWalletAsync(new WalletGenerateRequest { ChainType = provider.ChainType }, actor);
            if (created.IsError || created.Result is null) return AZOAResult<FungibleTokenResult>.Failure(created.Message);
            wallet = created.Result;
        }
        var creator = wallet.Address;
        if (target == FungibleMintTarget.PlatformReserve)
        {
            var account = await reserve.GetAccountAsync(provider.ChainType);
            if (account.IsError || account.Result is null) return AZOAResult<FungibleTokenResult>.Failure(account.Message);
            creator = account.Result.Address;
        }
        var signing = (target == FungibleMintTarget.PlatformReserve ? SigningContext.PlatformFor(provider.ChainType)
            : SigningContext.ForUser(actor, wallet.Id).ForChain(provider.ChainType)) with { Scope = AzoaScopes.TokenCreateSign };
        if (tenant is { } acting) signing = signing.ActingAs(acting, AzoaScopes.TokenCreateSign, actor);
        var intent = new FungibleCreationIntent(id, 0, actor, tenant, provider.ChainType, network, creator, signing,
            request.Name, request.UnitName, request.Total.ToString(CultureInfo.InvariantCulture), request.Decimals, false,
            creator, creator, creator, creator);
        AZOAResult<SigningAdmissionAuthority> bound;
        if (request.ActionAuthority is { } action)
        {
            var execution = await executions.GetByRunAndNodeAsync(action.RunId, action.NodeId);
            var witness = execution.Result?.SigningAuthorityJson is { } json ? JsonSerializer.Deserialize<WorkflowSigningWitness>(json) : null;
            if (witness is null || witness.Action != action)
                return AZOAResult<FungibleTokenResult>.Failure("The frozen workflow requires participant signing authorization.");
            bound = await signingAuthority.BindWorkflowAsync(witness, wallet.Id, provider.ChainType, wallet.Address);
        }
        else
        {
            if (request.AuthenticationWitness is not { } credential || credential.ActorUserId != actor || credential.TenantId != tenant)
                return AZOAResult<FungibleTokenResult>.Failure("The original authenticated signing credential is required.");
            bound = await signingAuthority.BindDirectAsync(credential, wallet.Id, provider.ChainType, wallet.Address, [AzoaScopes.TokenCreateSign]);
        }
        if (bound.IsError || bound.Result is null) return AZOAResult<FungibleTokenResult>.Failure(bound.Message);
        var command = new FungibleCreationCommand(actor, tenant, key, wallet.Id, wallet.Address, provisioned,
            target.Value.ToString(), request.HolonId, intent, request.ActionAuthority, requestHash, bound.Result);
        return Project(await lifecycle.AdmitAsync(command), actor, false);
    }

    private static AZOAResult<FungibleTokenResult> Project(AZOAResult<FungibleCreationView> result, Guid actor, bool replayed)
        => result.IsError || result.Result is null ? AZOAResult<FungibleTokenResult>.Failure(result.Message)
            : AZOAResult<FungibleTokenResult>.Success(new FungibleTokenResult
            {
                UserId = actor, WalletId = result.Result.WalletId, WalletAddress = result.Result.WalletAddress,
                WalletProvisioned = result.Result.WalletProvisioned, AssetId = result.Result.AssetId ?? "", Replayed = replayed,
                Operation = result.Result,
            });
}
