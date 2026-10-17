using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Fences a selected immutable cost receipt and database expiry with fresh NFT admission.</summary>
internal static class NftProtocolCostAdmissionFence
{
    internal const string FailureMarker = "NFT_PROTOCOL_COST_CHANGED";

    internal static bool Matches(NftLifecycle row, NftLifecycleCommand command)
    {
        var quote = command.ProtocolCostQuote;
        if (quote is null) return command.Metadata?.ProtocolCostAuthorization is null;
        return command.Metadata is not null && NftProtocolCostContract.IsAuthorized(quote, command.Metadata.ProtocolCostAuthorization) &&
            row.Operation == "Mint" && row.Stage == "Create" && row.State == "Preparing" && row.PreparationGeneration == 0 &&
            quote.Stage == row.Stage && quote.PreparationGeneration == row.PreparationGeneration && quote.LifecycleId == row.Id &&
            quote.ActorUserId == command.Actor && quote.ActingTenantId == command.ActingTenantId &&
            quote.PayerWalletId == command.ActorWalletId && quote.PayerAddress == command.ActorAddress &&
            quote.ChainType == row.ChainType && quote.Network.ToString() == row.Network &&
            row.ActorUserId == command.Actor.ToString("N") && row.RecipientUserId == command.Recipient.ToString("N") &&
            row.RequestHash == NftLifecycleIdentity.Fingerprint(row.RequestJson) &&
            quote.DraftHash == NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, command.Metadata);
    }

    internal static IReadOnlyList<SurrealQuery> Create(NftProtocolCostQuote quote)
    {
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(quote));
        // raw: exact immutable offer and database-time freshness are asserted within lifecycle creation.
        return
        [
            SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<NftProtocolCostQuoteRecord>.Key(quote.QuoteId),
                "nft_cost_receipt", "_nft_cost_receipt"),
            SurrealQuery.Of("""
                IF $_nft_cost_receipt = NONE OR $_nft_cost_receipt.quote_json != $_nft_cost_json
                    OR $_nft_cost_receipt.quote_hash != $_nft_cost_hash
                    OR $_nft_cost_receipt.actor_user_id != $_nft_cost_actor
                    OR $_nft_cost_receipt.acting_tenant_id != $_nft_cost_tenant
                    OR $_nft_cost_receipt.lifecycle_id != $_nft_cost_lifecycle
                    OR $_nft_cost_receipt.created_at != $_nft_cost_created
                    OR $_nft_cost_receipt.expires_at != $_nft_cost_expiry
                    OR $_nft_cost_expiry <= time::now()
                { THROW 'NFT_PROTOCOL_COST_CHANGED' }
                """).WithParam("_nft_cost_json", json).WithParam("_nft_cost_hash", NftProtocolCostContract.Hash(quote))
                .WithParam("_nft_cost_actor", quote.ActorUserId.ToString("N"))
                .WithParam("_nft_cost_tenant", quote.ActingTenantId.ToString("N"))
                .WithParam("_nft_cost_lifecycle", quote.LifecycleId)
                .WithParam("_nft_cost_created", quote.CreatedAt).WithParam("_nft_cost_expiry", quote.ExpiresAt),
        ];
    }
}
