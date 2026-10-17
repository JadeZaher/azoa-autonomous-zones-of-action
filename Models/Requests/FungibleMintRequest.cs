namespace AZOA.WebAPI.Models.Requests;

/// <summary>Node-selected custody destination for a new fungible asset.</summary>
public enum FungibleMintTarget
{
    PlatformReserve = 0,
    CallerWallet = 1,
}

/// <summary>Direct request to admit a fungible creation and obtain its recoverable operation receipt.</summary>
public class FungibleMintRequest : FungibleTokenCreateRequest { }
