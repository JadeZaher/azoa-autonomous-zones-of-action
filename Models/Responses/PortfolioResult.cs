namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Render-ready portfolio read-model (fungible-mint-and-render-model, §11.5).
/// Aggregates chain truth into EVERYTHING the frontend needs to render the
/// wallet's holdings in ONE call — no second round-trip per asset. Chain stays the
/// source of truth; AZOA stores no balance (the native + per-asset amounts are
/// read live and shaped here).
///
/// The legacy scalar fields (<see cref="Balance"/>, <see cref="Symbol"/>,
/// <see cref="Nfts"/>) are retained for existing callers; the new
/// <see cref="Assets"/> list is the unified render-model — one entry per holding
/// (native coin, fungible token, NFT) carrying id, symbol/unit, name, decimals,
/// raw + display-formatted amount, chain, asset kind, and a metadata/icon ref.
/// </summary>
public class PortfolioResult
{
    public Guid WalletId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Symbol { get; set; } = "SOL";
    public List<NftHolding> Nfts { get; set; } = new();

    /// <summary>
    /// The unified, render-ready holdings list (§11.5). One entry per asset the
    /// frontend renders, with display-formatted amounts already computed so the UI
    /// needs no second call and no client-side decimals math.
    /// </summary>
    public List<PortfolioAsset> Assets { get; set; } = new();

    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The kind of holding, so the frontend can branch rendering without guessing.</summary>
public enum PortfolioAssetKind
{
    /// <summary>The chain's native coin (e.g. ALGO, SOL, ETH).</summary>
    Native,
    /// <summary>A fungible token (e.g. an Algorand ASA with a real supply).</summary>
    Fungible,
    /// <summary>A non-fungible token (a supply-1 holon).</summary>
    Nft
}

/// <summary>
/// A single render-ready holding. Carries both the <see cref="RawAmount"/> (base
/// units, the chain-truth integer string) and the <see cref="DisplayAmount"/>
/// (decimal-adjusted, human-readable) so the frontend renders directly.
/// </summary>
public class PortfolioAsset
{
    /// <summary>
    /// A stable id for the holding. The chain-native asset id for native/fungible
    /// tokens (e.g. the ASA id, or the chain symbol for the native coin), or the
    /// holon id for an NFT — whatever the UI keys on.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The kind of holding (native coin / fungible token / NFT).</summary>
    public PortfolioAssetKind Kind { get; set; }

    /// <summary>Short symbol / unit name / ticker (e.g. "ALGO", "USDC").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Decimal places used to derive <see cref="DisplayAmount"/> from <see cref="RawAmount"/>.</summary>
    public int Decimals { get; set; }

    /// <summary>The raw on-chain amount in base units (arbitrary-precision string — chain truth).</summary>
    public string RawAmount { get; set; } = "0";

    /// <summary>The decimal-adjusted, human-readable amount (e.g. "1.50") — precomputed for the UI.</summary>
    public string DisplayAmount { get; set; } = "0";

    /// <summary>The chain this asset lives on (e.g. "Algorand").</summary>
    public string Chain { get; set; } = string.Empty;

    /// <summary>Optional icon / metadata reference (e.g. an image URI) for the UI.</summary>
    public string? IconRef { get; set; }
}

public class NftHolding
{
    public Guid HolonId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TokenId { get; set; }
    public string? ImageUri { get; set; }
}

/// <summary>
/// A fungible-token (e.g. Algorand ASA) holding read from chain truth, carrying the
/// base-unit amount plus the asset's display metadata (unit name, name, decimals) so
/// the render-model can compute a decimal-adjusted display amount without a second
/// per-asset round-trip from the UI.
/// </summary>
public class FungibleHolding
{
    /// <summary>The chain-native asset id (e.g. the ASA id), as a string.</summary>
    public string AssetId { get; set; } = string.Empty;
    /// <summary>The raw on-chain amount in base units (chain-truth integer string).</summary>
    public string RawAmount { get; set; } = "0";
    /// <summary>Short unit name / ticker (e.g. "USDC").</summary>
    public string UnitName { get; set; } = string.Empty;
    /// <summary>Human-readable asset name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Decimal places used to derive the display amount from the raw amount.</summary>
    public int Decimals { get; set; }
}
