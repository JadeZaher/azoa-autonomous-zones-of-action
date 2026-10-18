using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Constructs the same participant-bound intent for cost quotation and transaction preparation.</summary>
public static class NftLifecycleIntentFactory
{
    public static NftTransactionIntent? Create(NftLifecycle row, NftLifecycleCommand command,
        ChainNetwork network, string? credentialAuthorityAddress, Guid actingTenantId)
    {
        var recipientSigns = NftStageConsentIdentity.RecipientSigns(row, command);
        var platform = command.Metadata.IsSoulbound && row.Stage is NftTransactionKinds.Create or NftTransactionKinds.Deliver or NftTransactionKinds.Revoke;
        if (platform && (string.IsNullOrWhiteSpace(command.PlatformAuthorityAddress)
            || !ChainAddressIdentity.Equivalent(row.ChainType, command.PlatformAuthorityAddress, credentialAuthorityAddress))) return null;
        var signerAddress = platform ? command.PlatformAuthorityAddress : recipientSigns ? command.RecipientAddress : command.ActorAddress;
        if (string.IsNullOrWhiteSpace(signerAddress)) return null;
        var context = platform ? SigningContext.PlatformFor(row.ChainType)
            : SigningContext.ForUser(recipientSigns ? command.Recipient : command.Actor,
                recipientSigns ? command.RecipientWalletId : command.ActorWalletId).ForChain(row.ChainType);
        var consent = NftStageConsentIdentity.Required(row, command) ? NftStageConsentIdentity.Current(row) : null;
        var signingTenant = consent is not null ? consent.Authority.Credential.TenantId ?? Guid.Empty
            : NftStageConsentIdentity.Required(row, command) ? actingTenantId : command.ActingTenantId;
        if (signingTenant != Guid.Empty)
            context = context.ActingAs(signingTenant, row.Operation == "Burn" ? AzoaScopes.NftBurn : row.Operation is "Transfer" or "Refund" ? AzoaScopes.TransferSign : row.Operation == "Revoke" ? AzoaScopes.CredentialRevokeSign : AzoaScopes.NftMint,
                recipientSigns ? command.Recipient : command.Actor);
        var quote = command.FeeQuote ?? throw new InvalidOperationException("NFT fee quote is missing.");
        var chargesFee = row.Stage is NftTransactionKinds.Transfer or NftTransactionKinds.Burn or NftTransactionKinds.Revoke or NftTransactionKinds.Deliver
            || (row.Stage == NftTransactionKinds.Create && !command.Metadata.IsSoulbound);
        var feeContext = SigningContext.ForUser(command.Actor, command.ActorWalletId).ForChain(row.ChainType);
        if (command.ActingTenantId != Guid.Empty)
            feeContext = feeContext.ActingAs(command.ActingTenantId, row.Operation == "Burn" ? AzoaScopes.NftBurn
                : row.Operation == "Revoke" ? AzoaScopes.CredentialRevokeSign
                : row.Operation is "Transfer" or "Refund" ? AzoaScopes.TransferSign : AzoaScopes.NftMint, command.Actor);
        var fee = chargesFee && quote.NativeBaseUnits != "0" ? new NftFeeIntent(quote, command.ActorAddress, feeContext) : null;
        var authorization = new NftOperationAuthorization(row.Id, row.Operation, row.Stage, row.PreparationGeneration,
            command.Actor, command.ActorAddress, feeContext, command.Metadata.IsSoulbound ? command.Actor : null,
            command.Metadata.IsSoulbound ? command.ActorAddress : null, command.Metadata.IsSoulbound ? feeContext : null,
            chargesFee, quote);
        return new NftTransactionIntent(row.Stage, row.ChainType, network, signerAddress,
            row.Operation == "Refund" ? command.ActorAddress : command.RecipientAddress, row.AssetId, command.Metadata.Name ?? "NFT", command.Metadata.MetadataURI,
            command.Metadata.IsSoulbound, context, command.SourceAddress, $"{row.Id}:{row.Stage}:{row.PreparationGeneration}", fee, authorization,
            row.ContractAddress ?? string.Empty,
            ProtocolCostQuote: row.Stage == NftTransactionKinds.Create
                ? row.PreparationGeneration == 0 ? command.ProtocolCostQuote : NftReplacementCostContract.Current(row)?.Quote
                : NftStageCostContract.Required(row) ? NftStageCostContract.Current(row)?.Quote : null);
    }
}
