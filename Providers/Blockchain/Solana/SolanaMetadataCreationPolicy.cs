namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Reviewed metadata creation policy paired with an independently provisioned executable hash.</summary>
internal sealed record SolanaMetadataCreationPolicy(string CodeHash, string SourceRevision)
{
    internal const string SupportedRevision = "353d01be4af3018e1daf49d0cd35ba9f731d9c9c";
    internal const int MetadataLength = 607;
    internal const int MasterEditionLength = 20;

    internal void RequireSupported()
    {
        if (SourceRevision != SupportedRevision || CodeHash is not { Length: 64 }
            || CodeHash.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Metadata creation requires a reviewed executable and supported immutable source policy.");
    }

    internal (ulong RetainedRent, ulong ProtocolLevy) Costs(byte[] rent)
    {
        RequireSupported();
        return (WttSolanaCostGuard.MinimumBalance(rent, MetadataLength),
            checked(WttSolanaCostGuard.MinimumBalance(rent, 1308) + 5440));
    }

    internal SolanaNftAccountCosts NftCosts(byte[] rent, bool credential)
    {
        var metadata = Costs(rent);
        return new(WttSolanaCostGuard.MinimumBalance(rent, Solnet.Programs.TokenProgram.MintAccountDataSize),
            WttSolanaCostGuard.MinimumBalance(rent, 165), metadata.RetainedRent,
            credential ? 0 : WttSolanaCostGuard.MinimumBalance(rent, MasterEditionLength), metadata.ProtocolLevy);
    }
}

/// <summary>Maximum payer allocations for newly created NFT accounts under the pinned Rent snapshot.</summary>
internal sealed record SolanaNftAccountCosts(ulong MintRent, ulong HoldingRent, ulong MetadataRent,
    ulong MasterEditionRent, ulong ProtocolLevy)
{
    internal ulong RetainedRent => checked(MintRent + HoldingRent + MetadataRent + MasterEditionRent);
}
