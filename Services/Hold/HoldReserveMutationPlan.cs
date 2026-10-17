using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Builds conserved partition changes for an atomic hold command transaction.</summary>
public sealed record HoldReserveMutationPlan(string LedgerId, long ExpectedVersion, string IdentityHash,
    JsonElement Partitions, ReservePartitionCounters Counters)
{
    /// <summary>Calculates one hold draw using the existing reserve accounting rules.</summary>
    public static AZOAResult<HoldReserveMutationPlan> Create(PlatformReserveLedger ledger, string partition,
        ReserveDrawKind kind, string quantity, string protocolIdentity)
    {
        if (kind is not (ReserveDrawKind.Hold or ReserveDrawKind.ReleaseHold or ReserveDrawKind.CancelHold)
            || ledger.Version is < 0 or long.MaxValue || string.IsNullOrWhiteSpace(protocolIdentity)
            || ledger.ProtocolIdentity != protocolIdentity
            || !BigInteger.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0 || amount.ToString(CultureInfo.InvariantCulture) != quantity)
            return AZOAResult<HoldReserveMutationPlan>.Failure("A bound reserve identity and exact positive hold amount are required.");
        var partitions = ReservePartitionCodec.Decode(ledger.Partitions, out var error);
        var normalized = ReservePartitionCodec.Normalize(partition);
        if (partitions is null) return AZOAResult<HoldReserveMutationPlan>.Failure(error);
        if (normalized != partition || !partitions.TryGetValue(normalized, out var previous))
            return AZOAResult<HoldReserveMutationPlan>.Failure("The original declared reserve partition is required.");
        if (!previous.TryApply(kind, amount, normalized, out var next, out error))
            return AZOAResult<HoldReserveMutationPlan>.Failure(error);
        partitions[normalized] = next;
        return AZOAResult<HoldReserveMutationPlan>.Success(new(ledger.Id, ledger.Version, ImmutableIdentity(ledger),
            ReservePartitionCodec.Encode(partitions), next));
    }

    /// <summary>Hashes immutable reserve identity independently of changing partition counters.</summary>
    public static string ImmutableIdentity(PlatformReserveLedger ledger)
        => CanonicalJson.Hash(new { ledger.Id, ledger.Chain, ledger.Network, ledger.ProtocolIdentity, ledger.AssetId,
            ledger.ReserveAddress, TenantId = SurrealRecordGuid.ParseOptional(ledger.TenantId), ledger.UnitName,
            ledger.Decimals, ledger.TotalSupply });
}
