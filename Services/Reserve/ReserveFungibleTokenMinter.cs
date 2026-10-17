using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Services.Reserve;

/// <inheritdoc/>
public sealed class ReserveFungibleTokenMinter(IFungibleCreationService lifecycle, IPlatformTokenStore definitions,
    IBlockchainProviderFactory providers, IPlatformReserve reserve) : IPlatformTokenMinter
{
    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleTokenResult>> MintIntoReserveAsync(Guid operatorUserId,
        FungibleTokenCreateRequest request, string operationKey, CancellationToken ct = default)
    {
        if (operatorUserId != NodeOperatorIdentity.UserId || request.AuthenticationWitness is not { } credential
            || credential.ActorUserId != operatorUserId || credential.TokenUse != AzoaClaims.TokenUseNodeOperator || credential.TenantId is not null)
            return AZOAResult<FungibleTokenResult>.Failure("Actual node-operator credentials are required for platform genesis.");
        var network = request.Network ?? providers.DefaultNetwork;
        var rows = await definitions.ListForNetworkAsync(network.ToString(), ct);
        if (rows.IsError) return AZOAResult<FungibleTokenResult>.Failure(rows.Message);
        var row = rows.Result?.SingleOrDefault(value => value.GenesisOperationKey == operationKey);
        if (row is null || row.Chain != request.ChainType || row.Name != request.Name || row.UnitName != request.UnitName
            || row.Decimals != request.Decimals || row.TotalSupply != request.Total.ToString(CultureInfo.InvariantCulture))
            return AZOAResult<FungibleTokenResult>.Failure("The pending platform genesis definition does not match the requested creation.");
        var id = FungibleCreationIdentity.OperationId(operatorUserId, null, operationKey);
        var existing = await lifecycle.GetAsync(id, operatorUserId, null, ct);
        if (!existing.IsError && existing.Result is not null) return Project(existing, operatorUserId, true);
        var account = await reserve.GetAccountAsync(row.Chain, ct);
        if (account.IsError || account.Result?.Address != row.ReserveAddress)
            return AZOAResult<FungibleTokenResult>.Failure("The configured reserve no longer matches the original genesis definition.");
        IBlockchainProvider provider;
        try { provider = providers.GetProvider(row.Chain, network); }
        catch (BlockchainProviderNotFoundException) { return AZOAResult<FungibleTokenResult>.Failure("The bound genesis provider is unavailable."); }
        if (!provider.TryGetModule<IFungibleCreationModule>(out var module))
            return AZOAResult<FungibleTokenResult>.Failure("The bound provider has no durable fungible creation capability.");
        var intent = new FungibleCreationIntent(id, 0, operatorUserId, null, row.Chain, network, row.ReserveAddress,
            SigningContext.PlatformFor(row.Chain) with { Scope = AzoaScopes.TokenCreateSign }, row.Name, row.UnitName, row.TotalSupply,
            row.Decimals, false, row.ReserveAddress, row.ReserveAddress, row.ReserveAddress, row.ReserveAddress);
        var quote = await module!.QuoteFungibleCreationAsync(intent, ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<FungibleTokenResult>.Failure(quote.Message);
        var purpose = new PlatformGenesisAuthority(1, credential, row.Id, operationKey,
            PlatformGenesisIdentity.DefinitionHash(intent, operationKey), quote.Result.ProtocolIdentity);
        var command = new FungibleCreationCommand(operatorUserId, null, operationKey, Guid.Empty, row.ReserveAddress, false,
            "PlatformReserve", null, intent, GenesisAuthority: purpose);
        var admitted = await lifecycle.AdmitAsync(command, ct);
        if (admitted.IsError || admitted.Result is null) return Project(admitted, operatorUserId, false);
        return Project(await lifecycle.OfferQuoteAsync(id, operatorUserId, null, ct), operatorUserId, false);
    }

    private static AZOAResult<FungibleTokenResult> Project(AZOAResult<FungibleCreationView> result, Guid actor, bool replay)
        => result.IsError || result.Result is null ? AZOAResult<FungibleTokenResult>.Failure(result.Message)
            : AZOAResult<FungibleTokenResult>.Success(new() { UserId = actor, AssetId = result.Result.AssetId ?? "",
                WalletId = result.Result.WalletId, WalletAddress = result.Result.WalletAddress, Operation = result.Result, Replayed = replay });
}
