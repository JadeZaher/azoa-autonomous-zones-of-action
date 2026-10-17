// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Durable fungible creation admission and its observed issuance state.</summary>
public sealed class FungibleTokenResult
{
    public AZOA.WebAPI.Models.Blockchain.FungibleCreationView? Operation { get; set; }
    /// <summary>The user the token was launched for (the authorised principal).</summary>
    public Guid UserId { get; set; }

    /// <summary>The custodial wallet the token was launched from.</summary>
    public Guid WalletId { get; set; }

    /// <summary>The participant wallet address recorded at admission.</summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary><c>true</c> when this call provisioned a brand-new wallet.</summary>
    public bool WalletProvisioned { get; set; }

    /// <summary>The finalized chain asset identity, empty before confirmed issuance.</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>Whether this response replays the original keyed admission.</summary>
    public bool Replayed { get; set; }
}
