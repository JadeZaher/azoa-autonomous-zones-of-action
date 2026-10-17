using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Helpers;

/// <summary>Maps each durable NFT operation to its original participant signing scope.</summary>
public static class NftSigningScope
{
    public static string ForOperation(string operation) => operation switch
    {
        "Mint" => AzoaScopes.NftMint,
        "Burn" => AzoaScopes.NftBurn,
        "Revoke" => AzoaScopes.CredentialRevokeSign,
        "Transfer" or "Refund" => AzoaScopes.TransferSign,
        _ => throw new InvalidDataException("Unknown NFT operation signing scope."),
    };
}
