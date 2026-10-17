namespace AZOA.WebAPI.Models.Requests;

/// <summary>Claims the identity authorized by an invite with a user-chosen credential.</summary>
public class ClaimUserRequest
{
    /// <summary>Expected identity retained for ordinary credential login after response loss.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>Required live invitation; the persisted invitation determines authority.</summary>
    public string? ClaimToken { get; set; }

    /// <summary>A user-chosen password to set as the user's own login credential.</summary>
    public string? NewPassword { get; set; }

    /// <summary>Wallet address for a wallet-challenge credential (paired with the signature).</summary>
    public string? Address { get; set; }

    /// <summary>Chain type for the wallet-challenge credential.</summary>
    public string? ChainType { get; set; }

    /// <summary>Signature over the wallet challenge domain message proving control of the wallet.</summary>
    public string? Signature { get; set; }

    /// <summary>OPTIONAL exact-equality echo of the signed domain message (see WalletVerifyRequest).</summary>
    public string? Message { get; set; }
}
