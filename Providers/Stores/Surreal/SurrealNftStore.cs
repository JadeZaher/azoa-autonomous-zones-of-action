using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Json;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using GeneratedNft = AZOA.WebAPI.Persistence.SurrealDb.Models.NftOwnership;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="INftStore"/>. Maps between legacy domain
/// models (<see cref="UserNFT"/>, <see cref="HolonNFTBinding"/>,
/// <see cref="WalletNFTBinding"/>) and SurrealDB tables:
/// <list type="bullet">
///   <item><c>nft_ownership</c> — generated POCO (<see cref="GeneratedNft"/>)</item>
///   <item><c>holon_nft_binding</c> — no generated POCO; uses inline record type</item>
///   <item><c>wallet_nft_binding</c> — no generated POCO; uses inline record type</item>
/// </list>
///
/// UserNFT composite lookup: (ChainType, ContractAddress, TokenId) with
/// <c>is_current = true</c> to select live ownership rows only.
/// </summary>
public sealed class SurrealNftStore : INftStore, ICredentialIssuerStore
{
    private const string HolonBindingTable  = "holon_nft_binding";
    private const string WalletBindingTable = "wallet_nft_binding";

    private static readonly JsonSerializerOptions BindingJsonOpts = SurrealJsonOptions.Default;

    private readonly ISurrealExecutor _executor;

    public SurrealNftStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── UserNFT ─────────────────────────────────────────────────────────────

    public async Task<AZOAResult<IUserNFT>> UpsertUserNFTAsync(
        IUserNFT userNFT, CancellationToken ct = default)
    {
        try
        {
            if (userNFT.Id == Guid.Empty)
                userNFT.Id = Guid.NewGuid();

            var poco   = ToNftPoco(userNFT);

            // Coercion-safe SET-based UPSERT (SurrealWriter): omits null option<>
            // fields (3.x rejects an explicit null on option<string> like
            // description) and type::string-wraps string columns.
            var q = SurrealWriter.Upsert(poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<GeneratedNft>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : userNFT;

            // The issuance path, made real. issuer_user_id is the ONLY column the
            // revocation authorization decision turns on, and ToNftPoco deliberately
            // never writes it -- so without this call every soulbound credential is
            // minted with a null issuer and the whole revocation rail is inert: every
            // revoke dies at "no recorded issuer". This is its one production writer.
            //
            // Why userNFT.UserId is the ISSUER and not the D19-forbidden "issuer inferred
            // from the holder": a soulbound row never changes hands. UserNFTService
            // refuses to transfer one, so user_id stays the minting party for the row's
            // whole life. The only writer that ever re-points it is
            // RecordCredentialRevokedAsync below, and by the time that runs the issuer is
            // already recorded, so the write-once guard inside SetIssuerUserIdAsync
            // rejects any attempt to re-stamp from it. The forbidden inference is reading
            // an issuer off current_owner at REVOKE time for an arbitrary row; that is
            // still never done, and a row that reaches revoke with no issuer still fails
            // closed.
            if (userNFT.IsSoulbound && userNFT.UserId != Guid.Empty)
            {
                var stamped = await SetIssuerUserIdAsync(userNFT.Id, userNFT.UserId, ct);
                if (stamped.IsError)
                    return new AZOAResult<IUserNFT>
                    {
                        IsError = true,
                        Result  = result,
                        Message =
                            $"Soulbound credential {userNFT.Id} was written but its issuer could not be "
                          + $"recorded ({stamped.Message}). A credential with no recorded issuer is "
                          + "permanently unrevocable, so the mint is reported as failed rather than "
                          + "quietly issuing one nobody can ever revoke."
                    };
            }

            return new AZOAResult<IUserNFT> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IUserNFT>().CaptureException(ex,
                $"SurrealNftStore.UpsertUserNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IUserNFT>> GetUserNFTByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(GeneratedNft.SchemaNameConst, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedNft>(q, ct);
            return new AZOAResult<IUserNFT>
            {
                IsError = row == null,
                Message = row == null ? "NFT not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IUserNFT>().CaptureException(ex,
                $"SurrealNftStore.GetUserNFTByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IUserNFT>> GetUserNFTByTokenIdAsync(
        string chainType, string nftContractAddress, string tokenId, CancellationToken ct = default)
    {
        try
        {
            // Composite lookup: (chain_type, contract_address, token_id) with is_current = true
            var q = SurrealQuery<GeneratedNft>.From()
                .Where(n => n.ChainType       == chainType)
                .Where(n => n.ContractAddress == nftContractAddress)
                .Where(n => n.TokenId         == tokenId)
                .Where(n => n.IsCurrent       == true)
                .Limit(1);

            var rows = await _executor.QueryAsync<GeneratedNft>(q, ct);
            var row  = rows.Count > 0 ? rows[0] : null;

            return new AZOAResult<IUserNFT>
            {
                IsError = row == null,
                Message = row == null ? "NFT not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IUserNFT>().CaptureException(ex,
                $"SurrealNftStore.GetUserNFTByTokenIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IUserNFT>>> GetUserNFTsByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        try
        {
            var userLink = SurrealLink.ToLink("user", SurrealId.ToSurrealId(userId));
            var q = SurrealQuery<GeneratedNft>.From()
                .Where(n => n.UserId == userLink);

            var rows = await _executor.QueryAsync<GeneratedNft>(q, ct);
            return new AZOAResult<IEnumerable<IUserNFT>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IUserNFT>>().CaptureException(ex,
                $"SurrealNftStore.GetUserNFTsByUserAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteUserNFTAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(GeneratedNft.SchemaNameConst, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedNft>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "NFT not found.", Result = false };

            var q = SurrealQuery.DeleteById(GeneratedNft.SchemaNameConst, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteUserNFTAsync failed: {ex.Message}");
        }
    }

    // ── HolonNFTBinding ───────────────────────────────────────────────────────

    public async Task<AZOAResult<IHolonNFTBinding>> UpsertHolonNFTBindingAsync(
        IHolonNFTBinding binding, CancellationToken ct = default)
    {
        try
        {
            if (binding.Id == Guid.Empty)
                binding.Id = Guid.NewGuid();

            var poco   = ToHolonBindingPoco(binding);
            var surrId = poco.Id;

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    HolonBindingTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<SurrealHolonBinding>(0, BindingJsonOpts);
            var result = saved.Count > 0 ? FromSurrealHolonBinding(saved[0]) : binding;

            return new AZOAResult<IHolonNFTBinding> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IHolonNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.UpsertHolonNFTBindingAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IHolonNFTBinding>> GetHolonNFTBindingByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q   = SurrealQuery.SelectById(HolonBindingTable, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<SurrealHolonBinding>(q, ct);
            return new AZOAResult<IHolonNFTBinding>
            {
                IsError = row == null,
                Message = row == null ? "Binding not found." : "Success",
                Result  = row == null ? null : FromSurrealHolonBinding(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IHolonNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.GetHolonNFTBindingByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonNFTBindingsByUserNFTAsync(
        Guid userNFTId, CancellationToken ct = default)
    {
        try
        {
            var userNftId = SurrealId.ToSurrealId(userNFTId);
            var q = SurrealQuery<SurrealHolonBinding>.From()
                .Where(b => b.UserNftId == userNftId);

            var rows = await _executor.QueryAsync<SurrealHolonBinding>(q, ct);
            return new AZOAResult<IEnumerable<IHolonNFTBinding>>
            {
                Result  = rows.Select(FromSurrealHolonBinding).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IHolonNFTBinding>>().CaptureException(ex,
                $"SurrealNftStore.GetHolonNFTBindingsByUserNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteHolonNFTBindingAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(HolonBindingTable, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<SurrealHolonBinding>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

            var q = SurrealQuery.DeleteById(HolonBindingTable, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteHolonNFTBindingAsync failed: {ex.Message}");
        }
    }

    // ── WalletNFTBinding ──────────────────────────────────────────────────────

    public async Task<AZOAResult<IWalletNFTBinding>> UpsertWalletNFTBindingAsync(
        IWalletNFTBinding binding, CancellationToken ct = default)
    {
        try
        {
            if (binding.Id == Guid.Empty)
                binding.Id = Guid.NewGuid();

            var poco   = ToWalletBindingPoco(binding);
            var surrId = poco.Id;

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    WalletBindingTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<SurrealWalletBinding>(0, BindingJsonOpts);
            var result = saved.Count > 0 ? FromSurrealWalletBinding(saved[0]) : binding;

            return new AZOAResult<IWalletNFTBinding> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IWalletNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.UpsertWalletNFTBindingAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IWalletNFTBinding>> GetWalletNFTBindingByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q   = SurrealQuery.SelectById(WalletBindingTable, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<SurrealWalletBinding>(q, ct);
            return new AZOAResult<IWalletNFTBinding>
            {
                IsError = row == null,
                Message = row == null ? "Binding not found." : "Success",
                Result  = row == null ? null : FromSurrealWalletBinding(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IWalletNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.GetWalletNFTBindingByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletNFTBindingsByUserNFTAsync(
        Guid userNFTId, CancellationToken ct = default)
    {
        try
        {
            var userNftId = SurrealId.ToSurrealId(userNFTId);
            var q = SurrealQuery<SurrealWalletBinding>.From()
                .Where(b => b.UserNftId == userNftId);

            var rows = await _executor.QueryAsync<SurrealWalletBinding>(q, ct);
            return new AZOAResult<IEnumerable<IWalletNFTBinding>>
            {
                Result  = rows.Select(FromSurrealWalletBinding).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IWalletNFTBinding>>().CaptureException(ex,
                $"SurrealNftStore.GetWalletNFTBindingsByUserNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteWalletNFTBindingAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(WalletBindingTable, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<SurrealWalletBinding>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

            var q = SurrealQuery.DeleteById(WalletBindingTable, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteWalletNFTBindingAsync failed: {ex.Message}");
        }
    }

    // ── Helpers — Id encoding ─────────────────────────────────────────────────



    // ── ICredentialIssuerStore — the D19 issuer column ────────────────────────
    // The issuer is read and written HERE rather than through the IUserNFT mapping
    // below because IUserNFT carries no issuer property, and the only alternative
    // carrier on that model — the caller-writable Attributes bag — would let a
    // holder name themselves issuer and revoke their own credential. The column is
    // the durable home; these two methods are its whole surface.

    /// <inheritdoc />
    public async Task<AZOAResult<CredentialOwnershipRecord?>> FindCurrentByAssetIdAsync(
        string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(assetId))
            return new AZOAResult<CredentialOwnershipRecord?>
            {
                IsError = true,
                Message = "A chain type and asset id are required to look up a credential."
            };

        try
        {
            // Live ownership only: is_current = false rows are transfer history and
            // must never authorize a revocation of the asset's present holder.
            var q = SurrealQuery<GeneratedNft>.From()
                .Where(n => n.ChainType == chainType)
                .Where(n => n.TokenId   == assetId)
                .Where(n => n.IsCurrent == true)
                .Limit(1);

            var rows = await _executor.QueryAsync<GeneratedNft>(q, ct);
            var row  = rows.Count > 0 ? rows[0] : null;

            return new AZOAResult<CredentialOwnershipRecord?>
            {
                Message = row is null ? "Credential not found." : "Success",
                Result  = row is null ? null : ToCredentialRecord(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<CredentialOwnershipRecord?>().CaptureException(ex,
                $"SurrealNftStore.FindCurrentByAssetIdAsync failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> SetIssuerUserIdAsync(
        Guid credentialId, Guid issuerUserId, CancellationToken ct = default)
    {
        if (credentialId == Guid.Empty || issuerUserId == Guid.Empty)
            return new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = "A credential id and a non-empty issuer id are required."
            };

        try
        {
            // Targeted single-column UPDATE rather than a full-row upsert: the mint
            // path stamps the issuer without having to round-trip (and therefore
            // risk rewriting) every other column on the row.
            //
            // WHERE issuer_user_id IS NONE makes the column write-ONCE in the database
            // itself, not merely by convention in the caller. The issuer decides who may
            // claw a credential back, so a later write that re-pointed it -- from an
            // ownership change, a metadata edit, or a racing request -- would hand the
            // revocation authority to a different party.
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET issuer_user_id = $_issuer WHERE issuer_user_id IS NONE RETURN AFTER")
                .WithParam("_t", GeneratedNft.SchemaNameConst)
                .WithParam("_id", SurrealId.ToSurrealId(credentialId))
                .WithParam("_issuer", SurrealRecordParam.Of("user", SurrealId.ToSurrealId(issuerUserId)));

            var resp = await _executor.ExecuteAsync(query, ct);
            resp.EnsureAllOk();

            if (resp.GetValues<GeneratedNft>(0).FirstOrDefault() is not null)
                return new AZOAResult<bool> { Result = true, Message = "Issuer recorded." };

            // Zero rows matched: either the row does not exist, or it already carries an
            // issuer. Read it to say WHICH -- re-stamping a DIFFERENT issuer is refused,
            // but re-stamping the same one is the idempotent retry of a mint and must
            // not fail it.
            var existing = await _executor.QuerySingleAsync<GeneratedNft>(
                SurrealQuery.SelectById(GeneratedNft.SchemaNameConst, SurrealId.ToSurrealId(credentialId)), ct);
            if (existing is null)
                return new AZOAResult<bool> { IsError = true, Result = false, Message = "Credential not found." };

            return ParseUserLink(existing.IssuerUserId) == issuerUserId
                ? new AZOAResult<bool> { Result = true, Message = "Issuer already recorded." }
                : new AZOAResult<bool>
                  {
                      IsError = true,
                      Result  = false,
                      Message =
                          $"Credential {credentialId} already records a different issuer. Refusing to "
                        + "re-point the anchor that decides who may revoke it."
                  };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealNftStore.SetIssuerUserIdAsync failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> RecordCredentialRevokedAsync(
        Guid credentialId, Guid issuerUserId, string issuerAddress, CancellationToken ct = default)
    {
        if (credentialId == Guid.Empty || issuerUserId == Guid.Empty || string.IsNullOrWhiteSpace(issuerAddress))
            return new AZOAResult<bool>
            {
                IsError = true,
                Result  = false,
                Message = "A credential id, an issuer, and the issuer's address are required to record a revocation."
            };

        try
        {
            // The SAME live row, re-pointed at the issuer -- NOT a new row and NOT
            // is_current = false. A confirmed clawback MOVED the asset, it did not
            // destroy it, so the (chain, contract, token_id, is_current = true) singleton
            // must keep naming exactly one row or every credential read surface loses the
            // asset entirely. issuer_user_id is untouched: the party that issued a
            // credential does not change by taking it back.
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET user_id = $_issuer, current_owner = type::string($_owner), last_transfer_date = time::now() RETURN AFTER")
                .WithParam("_t", GeneratedNft.SchemaNameConst)
                .WithParam("_id", SurrealId.ToSurrealId(credentialId))
                .WithParam("_issuer", SurrealRecordParam.Of("user", SurrealId.ToSurrealId(issuerUserId)))
                .WithParam("_owner", issuerAddress.Trim());

            var resp = await _executor.ExecuteAsync(query, ct);
            resp.EnsureAllOk();

            var saved = resp.GetValues<GeneratedNft>(0).FirstOrDefault();
            return saved is null
                ? new AZOAResult<bool> { IsError = true, Result = false, Message = "Credential not found." }
                : new AZOAResult<bool> { Result = true, Message = "Ownership returned to the issuer." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealNftStore.RecordCredentialRevokedAsync failed: {ex.Message}");
        }
    }

    /// <summary>Projects the ownership POCO onto the credential view, issuer included.</summary>
    private static CredentialOwnershipRecord ToCredentialRecord(GeneratedNft p) => new()
    {
        Id            = SurrealId.FromSurrealId(p.Id),
        HolderUserId  = SurrealId.FromSurrealId(SurrealLink.FromLink(p.UserId)!),
        HolderAddress = p.CurrentOwner,
        IssuerUserId  = ParseUserLink(p.IssuerUserId),
        IsSoulbound   = p.IsSoulbound,
        ChainType     = p.ChainType,
        TokenId       = p.TokenId,
    };

    /// <summary>
    /// Reads an optional <c>user</c> record link as a Guid. Anything unreadable comes
    /// back null so the caller fails closed rather than authorizing on a guess.
    /// </summary>
    private static Guid? ParseUserLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var bare = SurrealLink.FromLink(link);
        if (string.IsNullOrWhiteSpace(bare)) return null;
        try
        {
            var id = SurrealId.FromSurrealId(bare);
            return id == Guid.Empty ? null : id;
        }
        catch
        {
            return null;
        }
    }

    // ── UserNFT mapping ─────────────────────────────────────────────────────

    private static GeneratedNft ToNftPoco(IUserNFT n)
    {
        // ALWAYS serialize (empty → `{}`) so the SET-based upsert REPLACES the
        // column; see Providers/Stores/Surreal/AGENTS.md §set-omits-null.
        JsonElement? attributesJson = JsonSerializer.SerializeToElement(n.Attributes, SurrealJsonOptions.Default);

        return new GeneratedNft
        {
            Id                = SurrealId.ToSurrealId(n.Id),
            UserId          = SurrealLink.ToLink("user", SurrealId.ToSurrealId(n.UserId))!,
            ChainType         = n.ChainType,
            ContractAddress   = n.NFTContractAddress,
            TokenId           = n.TokenId,
            TokenStandard     = n.TokenStandard,
            MetadataUri       = n.MetadataURI,
            ImageUri          = n.ImageURI,
            Name              = n.Name,
            Description       = n.Description,
            Attributes        = attributesJson,
            RoyaltyPercentage = n.RoyaltyPercentage,
            RoyaltyRecipient  = n.RoyaltyRecipient,
            IsSoulbound       = n.IsSoulbound,
            IsTransferable    = n.IsTransferable,
            IsCurrent         = true,
            CurrentOwner      = n.CurrentOwner,
            // IssuerUserId (D19) is deliberately LEFT NULL here and is NOT the
            // §set-omits-null case above: SurrealWriter omits null option<> fields
            // from the SET, so an ordinary UserNFT upsert (a transfer, a metadata
            // edit) preserves whatever issuer the row already carries instead of
            // wiping the authorization anchor. It is written only through
            // ICredentialIssuerStore.SetIssuerUserIdAsync.
            IsActive          = n.IsActive,
            MintedDate        = new DateTimeOffset(
                                    DateTime.SpecifyKind(n.MintedDate, DateTimeKind.Utc)),
            LastTransferDate  = n.LastTransferDate.HasValue
                                ? new DateTimeOffset(
                                      DateTime.SpecifyKind(n.LastTransferDate.Value, DateTimeKind.Utc))
                                : null
        };
    }

    private static UserNFT FromPoco(GeneratedNft p)
    {
        Dictionary<string, string> attributes = new();
        if (p.Attributes.HasValue)
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    p.Attributes.Value.GetRawText(), SurrealJsonOptions.Default);
                if (deserialized != null)
                    attributes = deserialized;
            }
            catch { /* best-effort */ }
        }

        return new UserNFT
        {
            Id                  = SurrealId.FromSurrealId(p.Id),
            UserId            = SurrealId.FromSurrealId(SurrealLink.FromLink(p.UserId)!),
            ChainType           = p.ChainType,
            NFTContractAddress  = p.ContractAddress,
            TokenId             = p.TokenId,
            TokenStandard       = p.TokenStandard,
            MetadataURI         = p.MetadataUri,
            ImageURI            = p.ImageUri,
            Name                = p.Name,
            Description         = p.Description,
            Attributes          = attributes,
            RoyaltyPercentage   = p.RoyaltyPercentage,
            RoyaltyRecipient    = p.RoyaltyRecipient,
            IsSoulbound         = p.IsSoulbound,
            IsTransferable      = p.IsTransferable,
            // IssuerUserId is not projected onto UserNFT: IUserNFT has no issuer
            // property, and its only writable carrier (Attributes) is caller-supplied,
            // which would make the revoke authorization forgeable by the holder. Read
            // it through ICredentialIssuerStore.FindCurrentByAssetIdAsync instead.
            CurrentOwner        = p.CurrentOwner,
            IsActive            = p.IsActive,
            MintedDate          = p.MintedDate.UtcDateTime,
            LastTransferDate    = p.LastTransferDate?.UtcDateTime
        };
    }

    // ── HolonNFTBinding mapping ───────────────────────────────────────────────

    private static SurrealHolonBinding ToHolonBindingPoco(IHolonNFTBinding b)
    {
        JsonElement? permissionsJson = null;
        if (b.Permissions.Count > 0)
        {
            var raw = JsonSerializer.Serialize(b.Permissions, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            permissionsJson = doc.RootElement.Clone();
        }

        return new SurrealHolonBinding
        {
            Id              = SurrealId.ToSurrealId(b.Id),
            HolonId         = SurrealId.ToSurrealId(b.HolonId),
            UserNftId     = SurrealId.ToSurrealId(b.UserNFTId),
            Role            = b.Role,
            PermissionLevel = b.PermissionLevel,
            Permissions     = permissionsJson,
            CreatedDate     = new DateTimeOffset(
                                  DateTime.SpecifyKind(b.CreatedDate, DateTimeKind.Utc)),
            LastUpdatedDate = b.LastUpdatedDate.HasValue
                              ? new DateTimeOffset(
                                    DateTime.SpecifyKind(b.LastUpdatedDate.Value, DateTimeKind.Utc))
                              : null,
            IsActive        = b.IsActive
        };
    }

    private static HolonNFTBinding FromSurrealHolonBinding(SurrealHolonBinding s)
    {
        Dictionary<string, string> perms = new();
        if (s.Permissions.HasValue)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    s.Permissions.Value.GetRawText(), SurrealJsonOptions.Default);
                if (d != null) perms = d;
            }
            catch { /* best-effort */ }
        }

        return new HolonNFTBinding
        {
            Id              = SurrealId.FromSurrealId(s.Id),
            HolonId         = SurrealId.FromSurrealId(s.HolonId),
            UserNFTId     = SurrealId.FromSurrealId(s.UserNftId),
            Role            = s.Role,
            PermissionLevel = s.PermissionLevel,
            Permissions     = perms,
            CreatedDate     = s.CreatedDate.UtcDateTime,
            LastUpdatedDate = s.LastUpdatedDate?.UtcDateTime,
            IsActive        = s.IsActive
        };
    }

    // ── WalletNFTBinding mapping ──────────────────────────────────────────────

    private static SurrealWalletBinding ToWalletBindingPoco(IWalletNFTBinding b)
    {
        JsonElement? permissionsJson = null;
        if (b.AccessPermissions.Count > 0)
        {
            var raw = JsonSerializer.Serialize(b.AccessPermissions, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            permissionsJson = doc.RootElement.Clone();
        }

        return new SurrealWalletBinding
        {
            Id                = SurrealId.ToSurrealId(b.Id),
            WalletId          = SurrealId.ToSurrealId(b.WalletId),
            UserNftId       = SurrealId.ToSurrealId(b.UserNFTId),
            BindingType       = b.BindingType,
            AccessLevel       = b.AccessLevel,
            AccessPermissions = permissionsJson,
            CreatedDate       = new DateTimeOffset(
                                    DateTime.SpecifyKind(b.CreatedDate, DateTimeKind.Utc)),
            LastUpdatedDate   = b.LastUpdatedDate.HasValue
                                ? new DateTimeOffset(
                                      DateTime.SpecifyKind(b.LastUpdatedDate.Value, DateTimeKind.Utc))
                                : null,
            IsActive          = b.IsActive
        };
    }

    private static WalletNFTBinding FromSurrealWalletBinding(SurrealWalletBinding s)
    {
        Dictionary<string, string> perms = new();
        if (s.AccessPermissions.HasValue)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    s.AccessPermissions.Value.GetRawText(), SurrealJsonOptions.Default);
                if (d != null) perms = d;
            }
            catch { /* best-effort */ }
        }

        return new WalletNFTBinding
        {
            Id                = SurrealId.FromSurrealId(s.Id),
            WalletId          = SurrealId.FromSurrealId(s.WalletId),
            UserNFTId       = SurrealId.FromSurrealId(s.UserNftId),
            BindingType       = s.BindingType,
            AccessLevel       = s.AccessLevel,
            AccessPermissions = perms,
            CreatedDate       = s.CreatedDate.UtcDateTime,
            LastUpdatedDate   = s.LastUpdatedDate?.UtcDateTime,
            IsActive          = s.IsActive
        };
    }

    // ── Inline SurrealDB record types for binding tables ──────────────────────
    // These tables have no generated POCOs (not in the Mermaid schema).
    // They are SCHEMALESS (SurrealDB defaults) — adapters manage the shape.

    private sealed class SurrealHolonBinding : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => HolonBindingTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("holon_id")]
        public string HolonId { get; set; } = string.Empty;

        [JsonPropertyName("user_nft_id")]
        public string UserNftId { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("permission_level")]
        public string? PermissionLevel { get; set; }

        [JsonPropertyName("permissions")]
        public JsonElement? Permissions { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("last_updated_date")]
        public DateTimeOffset? LastUpdatedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }

    private sealed class SurrealWalletBinding : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => WalletBindingTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("wallet_id")]
        public string WalletId { get; set; } = string.Empty;

        [JsonPropertyName("user_nft_id")]
        public string UserNftId { get; set; } = string.Empty;

        [JsonPropertyName("binding_type")]
        public string BindingType { get; set; } = string.Empty;

        [JsonPropertyName("access_level")]
        public string? AccessLevel { get; set; }

        [JsonPropertyName("access_permissions")]
        public JsonElement? AccessPermissions { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("last_updated_date")]
        public DateTimeOffset? LastUpdatedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
