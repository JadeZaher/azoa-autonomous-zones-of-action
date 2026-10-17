using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private Task<SolanaFinalized> RequireWttFinalityAsync(JsonElement transaction, WttStagePreparation prepared,
        WttSolanaWirePlan wire, IReadOnlyList<SolanaLookupTableSnapshot> tables, CancellationToken ct)
        => RequireSolanaFinalityAsync(transaction, prepared.TransactionId, prepared.SignedTransactionBase64,
            wire.MessageBase64, tables, UInt64Text.Parse(prepared.Reservation.NetworkFee), ct);
}
