using System.Globalization;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Builds the existing reserve ledger projection for an explicitly bound asset identity.</summary>
public static class ReserveAssetRegistrationPlan
{
    /// <summary>Compares immutable reserve registration identity independently of partition counters.</summary>
    public static bool SameIdentity(PlatformReserveLedger left, PlatformReserveLedger right)
        => left.Id == right.Id && left.AssetId == right.AssetId && left.Chain == right.Chain
           && left.Network == right.Network && left.ProtocolIdentity == right.ProtocolIdentity
           && left.ReserveAddress == right.ReserveAddress
           && SurrealRecordGuid.ParseOptional(left.TenantId) == SurrealRecordGuid.ParseOptional(right.TenantId)
           && left.UnitName == right.UnitName && left.Decimals == right.Decimals && left.TotalSupply == right.TotalSupply;

    /// <summary>Creates the immutable asset registration and empty partition ledger.</summary>
    public static PlatformReserveLedger Create(ReserveAssetRegistration registration, ReserveAccount account,
        string network, string? protocolIdentity, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(registration.AssetId) || registration.TotalSupply <= 0
            || string.IsNullOrWhiteSpace(account.Address) || registration.ChainType != account.ChainType
            || network is not ("Devnet" or "Testnet" or "Mainnet"))
            throw new ArgumentException("A complete bound reserve registration is required.");
        return new PlatformReserveLedger
        {
            Id = PlatformReserveLedger.RecordIdFor(account.ChainType, network, registration.AssetId),
            AssetId = registration.AssetId, Chain = account.ChainType, Network = network, ProtocolIdentity = protocolIdentity,
            ReserveAddress = account.Address, TenantId = registration.TenantId is { } tenant ? SurrealLink.ToLink("user", tenant.ToString("N")) : null,
            UnitName = registration.UnitName, Decimals = registration.Decimals,
            TotalSupply = registration.TotalSupply.ToString(CultureInfo.InvariantCulture),
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>()),
            RecentDrawKeys = [], Version = 0, CreatedAt = now, UpdatedAt = now,
        };
    }
}
