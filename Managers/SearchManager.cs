using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class SearchManager : ISearchManager
{
    private readonly IUserStore _userStore;
    private readonly IHolonStore _holonStore;
    private readonly IWalletStore _walletStore;
    private readonly IBlockchainOperationStore _blockchainOperationStore;
    private readonly ISTARStore _starStore;

    public SearchManager(
        IUserStore userStore,
        IHolonStore holonStore,
        IWalletStore walletStore,
        IBlockchainOperationStore blockchainOperationStore,
        ISTARStore starStore)
    {
        _userStore = userStore;
        _holonStore = holonStore;
        _walletStore = walletStore;
        _blockchainOperationStore = blockchainOperationStore;
        _starStore = starStore;
    }

    public async Task<AZOAResult<SearchResult>> SearchAsync(SearchRequest request, Guid? callerUserId, AZOARequest? providerRequest = null)
    {
        // Clamp page size
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var hits = new List<SearchHit>();
        var facets = new List<SearchFacet>();
        var query = request.Query?.ToLowerInvariant() ?? string.Empty;

        // Cross-tenant isolation: the caller may NOT enumerate another user's
        // private data. Sensitive entity types are force-scoped to callerUserId
        // and the caller-supplied request.UserId is IGNORED for them. Users are
        // the one intentionally-public type (already PII-free — see User section).
        // A null callerUserId fails closed (no sensitive rows returned at all).
        // Holon/STARODK additionally surface IsPublic rows via the owner-or-public
        // predicate below so the marketplace stays discoverable.

        // ─── Search Users ───
        if (request.EntityTypes.HasFlag(SearchableEntityType.User))
        {
            var users = await _userStore.GetAllAsync(default);
            if (!users.IsError && users.Result != null)
            {
                var filtered = users.Result.Where(a => MatchesUser(a, query, request));
                facets.Add(new SearchFacet { EntityType = SearchableEntityType.User, Count = filtered.Count(), Label = "Users" });
                hits.AddRange(filtered.Select(a => new SearchHit
                {
                    Id = a.Id,
                    EntityType = SearchableEntityType.User,
                    Title = a.Username,
                    Description = a.Title ?? string.Empty,
                    // Marketplace search is public — no PII (email/real name) may
                    // appear in results, and MatchesUser no longer matches on email
                    // either (hardening review M1: was an account-enumeration oracle).
                    Highlight = FindHighlight(a.Username, a.Title ?? string.Empty, query),
                    Fields = new Dictionary<string, object>
                    {
                        ["IsActive"] = a.IsActive
                    },
                    CreatedDate = a.CreatedDate
                }));
            }
        }

        // ─── Search Holons ───
        if (request.EntityTypes.HasFlag(SearchableEntityType.Holon))
        {
            var holons = await _holonStore.QueryAsync(null, default);
            if (!holons.IsError && holons.Result != null)
            {
                var filtered = holons.Result
                    .Where(h => callerUserId.HasValue && h.UserId == callerUserId.Value || h.IsPublic)
                    .Where(h => MatchesHolon(h, query, request));
                facets.Add(new SearchFacet { EntityType = SearchableEntityType.Holon, Count = filtered.Count(), Label = "Holons" });
                hits.AddRange(filtered.Select(h => new SearchHit
                {
                    Id = h.Id,
                    EntityType = SearchableEntityType.Holon,
                    Title = h.Name,
                    Description = h.Description,
                    Highlight = FindHighlight(h.Name, h.Description, query),
                    Fields = new Dictionary<string, object>
                    {
                        ["ProviderName"] = h.ProviderName,
                        ["ChainId"] = (object?)h.ChainId ?? "",
                        ["AssetType"] = (object?)h.AssetType ?? "",
                        ["IsActive"] = h.IsActive
                    },
                    CreatedDate = h.CreatedDate
                }));
            }
        }

        // ─── Search Wallets (H-2: sensitive — ALWAYS caller-scoped; never GetAllAsync) ───
        if (request.EntityTypes.HasFlag(SearchableEntityType.Wallet) && callerUserId.HasValue)
        {
            var walletsResult = await _walletStore.GetByUserAsync(callerUserId.Value, default);

            if (!walletsResult.IsError && walletsResult.Result != null)
            {
                var filtered = walletsResult.Result.Where(w => MatchesWallet(w, query, request));
                facets.Add(new SearchFacet { EntityType = SearchableEntityType.Wallet, Count = filtered.Count(), Label = "Wallets" });
                hits.AddRange(filtered.Select(w => new SearchHit
                {
                    Id = w.Id,
                    EntityType = SearchableEntityType.Wallet,
                    Title = w.Address,
                    Description = $"{w.ChainType}" + (string.IsNullOrEmpty(w.Label) ? "" : $" - {w.Label}"),
                    Highlight = FindHighlight(w.Address, w.Label ?? "", w.ChainType, query),
                    Fields = new Dictionary<string, object>
                    {
                        ["ChainType"] = w.ChainType,
                        ["IsDefault"] = w.IsDefault,
                        ["UserId"] = w.UserId
                    },
                    CreatedDate = w.CreatedDate
                }));
            }
        }

        // ─── Search Blockchain Operations (H-2: sensitive — ALWAYS caller-scoped; the all-users iteration is removed) ───
        if (request.EntityTypes.HasFlag(SearchableEntityType.BlockchainOperation) && callerUserId.HasValue)
        {
            var opsResult = await _blockchainOperationStore.GetByUserAsync(callerUserId.Value, default);
            IEnumerable<IBlockchainOperation> ops = opsResult.Result ?? Enumerable.Empty<IBlockchainOperation>();

            var filteredOps = ops.Where(o => MatchesBlockchainOp(o, query, request));
            facets.Add(new SearchFacet { EntityType = SearchableEntityType.BlockchainOperation, Count = filteredOps.Count(), Label = "Operations" });
            hits.AddRange(filteredOps.Select(o => new SearchHit
            {
                Id = o.Id,
                EntityType = SearchableEntityType.BlockchainOperation,
                Title = o.OperationType,
                Description = $"Status: {o.Status}",
                Highlight = FindHighlight(o.OperationType, o.Status, query),
                Fields = new Dictionary<string, object>
                {
                    ["Status"] = o.Status,
                    ["WalletId"] = (object?)o.WalletId ?? Guid.Empty
                },
                CreatedDate = o.CreatedDate
            }));
        }

        // ─── Search STARODKs ───
        if (request.EntityTypes.HasFlag(SearchableEntityType.STARODK))
        {
            var stars = await _starStore.GetAllAsync(default);
            if (!stars.IsError && stars.Result != null)
            {
                var filtered = stars.Result
                    .Where(s => callerUserId.HasValue && s.UserId == callerUserId.Value || s.IsPublic)
                    .Where(s => MatchesSTARODK(s, query, request));
                facets.Add(new SearchFacet { EntityType = SearchableEntityType.STARODK, Count = filtered.Count(), Label = "STAR ODKs" });
                hits.AddRange(filtered.Select(s => new SearchHit
                {
                    Id = s.Id,
                    EntityType = SearchableEntityType.STARODK,
                    Title = s.Name,
                    Description = s.Description,
                    Highlight = FindHighlight(s.Name, s.Description, query),
                    Fields = new Dictionary<string, object>
                    {
                        ["TargetChain"] = (object?)s.TargetChain ?? "",
                        ["IsActive"] = s.IsActive
                    },
                    CreatedDate = DateTime.UtcNow
                }));
            }
        }

        // ─── Sort ───
        hits = request.SortBy?.ToLowerInvariant() switch
        {
            "name" => request.SortDescending
                ? hits.OrderByDescending(h => h.Title).ToList()
                : hits.OrderBy(h => h.Title).ToList(),
            _ => request.SortDescending
                ? hits.OrderByDescending(h => h.CreatedDate).ToList()
                : hits.OrderBy(h => h.CreatedDate).ToList()
        };

        var totalCount = hits.Count;

        // ─── Paginate ───
        var skip = (request.Page - 1) * pageSize;
        var pageHits = hits.Skip(skip).Take(pageSize).ToList();

        return new AZOAResult<SearchResult>
        {
            Result = new SearchResult
            {
                Query = request.Query ?? string.Empty,
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = pageSize,
                Hits = pageHits,
                Facets = facets
            },
            Message = "Search completed."
        };
    }

    public async Task<AZOAResult<List<SearchFacet>>> GetFacetsAsync(Guid? callerUserId, AZOARequest? providerRequest = null)
    {
        var facets = new List<SearchFacet>();

        // Users are the one public type — global count is fine.
        var users = await _userStore.GetAllAsync(default);
        if (!users.IsError && users.Result != null)
            facets.Add(new SearchFacet { EntityType = SearchableEntityType.User, Count = users.Result.Count(), Label = "Users" });

        // Holon/STARODK counts: caller-owned OR public (mirror SearchAsync scoping — no global leak).
        var holons = await _holonStore.QueryAsync(null, default);
        if (!holons.IsError && holons.Result != null)
            facets.Add(new SearchFacet { EntityType = SearchableEntityType.Holon, Label = "Holons",
                Count = holons.Result.Count(h => callerUserId.HasValue && h.UserId == callerUserId.Value || h.IsPublic) });

        // Wallets: strictly the caller's own (never public).
        if (callerUserId.HasValue)
        {
            var wallets = await _walletStore.GetByUserAsync(callerUserId.Value, default);
            if (!wallets.IsError && wallets.Result != null)
                facets.Add(new SearchFacet { EntityType = SearchableEntityType.Wallet, Count = wallets.Result.Count(), Label = "Wallets" });
        }

        var stars = await _starStore.GetAllAsync(default);
        if (!stars.IsError && stars.Result != null)
            facets.Add(new SearchFacet { EntityType = SearchableEntityType.STARODK, Label = "STAR ODKs",
                Count = stars.Result.Count(s => callerUserId.HasValue && s.UserId == callerUserId.Value || s.IsPublic) });

        return new AZOAResult<List<SearchFacet>> { Result = facets, Message = "Facets computed." };
    }

    // ─── Matching helpers ───

    private bool MatchesUser(IUser a, string query, SearchRequest request)
    {
        if (!string.IsNullOrEmpty(query))
        {
            // Email is intentionally excluded from matching (hardening review M1):
            // this is a public marketplace search, and matching on email turns it
            // into an account-enumeration oracle even though email isn't echoed back.
            if (!ContainsText(a.Username, query) && !ContainsText(a.Title ?? string.Empty, query))
                return false;
        }
        if (request.UserId.HasValue && a.Id != request.UserId.Value) return false;
        if (request.CreatedAfter.HasValue && a.CreatedDate < request.CreatedAfter.Value) return false;
        if (request.CreatedBefore.HasValue && a.CreatedDate > request.CreatedBefore.Value) return false;
        return true;
    }

    private bool MatchesHolon(IHolon h, string query, SearchRequest request)
    {
        if (!string.IsNullOrEmpty(query))
        {
            if (!ContainsText(h.Name, query) && !ContainsText(h.Description, query) && !MetadataContains(h.Metadata, query))
                return false;
        }
        if (!string.IsNullOrEmpty(request.ChainId) && !h.ChainId?.Equals(request.ChainId, StringComparison.OrdinalIgnoreCase) == true) return false;
        if (!string.IsNullOrEmpty(request.AssetType) && !h.AssetType?.Equals(request.AssetType, StringComparison.OrdinalIgnoreCase) == true) return false;
        if (request.UserId.HasValue && h.UserId != request.UserId.Value) return false;
        if (request.CreatedAfter.HasValue && h.CreatedDate < request.CreatedAfter.Value) return false;
        if (request.CreatedBefore.HasValue && h.CreatedDate > request.CreatedBefore.Value) return false;
        return true;
    }

    private bool MatchesWallet(IWallet w, string query, SearchRequest request)
    {
        if (!string.IsNullOrEmpty(query))
        {
            if (!ContainsText(w.Address, query) && !ContainsText(w.Label ?? "", query) && !ContainsText(w.ChainType, query))
                return false;
        }
        if (!string.IsNullOrEmpty(request.ChainId) && !w.ChainType.Equals(request.ChainId, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.UserId.HasValue && w.UserId != request.UserId.Value) return false;
        if (request.CreatedAfter.HasValue && w.CreatedDate < request.CreatedAfter.Value) return false;
        if (request.CreatedBefore.HasValue && w.CreatedDate > request.CreatedBefore.Value) return false;
        return true;
    }

    private bool MatchesBlockchainOp(IBlockchainOperation o, string query, SearchRequest request)
    {
        if (!string.IsNullOrEmpty(query))
        {
            if (!ContainsText(o.OperationType, query) && !ContainsText(o.Status, query))
                return false;
        }
        if (request.UserId.HasValue && o.UserId != request.UserId.Value) return false;
        if (request.CreatedAfter.HasValue && o.CreatedDate < request.CreatedAfter.Value) return false;
        if (request.CreatedBefore.HasValue && o.CreatedDate > request.CreatedBefore.Value) return false;
        return true;
    }

    private bool MatchesSTARODK(ISTARODK s, string query, SearchRequest request)
    {
        if (!string.IsNullOrEmpty(query))
        {
            if (!ContainsText(s.Name, query) && !ContainsText(s.Description, query))
                return false;
        }
        if (!string.IsNullOrEmpty(request.ChainId) && !s.TargetChain?.Equals(request.ChainId, StringComparison.OrdinalIgnoreCase) == true) return false;
        if (request.UserId.HasValue && s.UserId != request.UserId.Value) return false;
        return true;
    }

    private static bool ContainsText(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool MetadataContains(Dictionary<string, string> metadata, string query) =>
        metadata.Values.Any(v => ContainsText(v, query));

    private static string? FindHighlight(params string[] fields)
    {
        foreach (var f in fields)
        {
            if (!string.IsNullOrEmpty(f)) return f;
        }
        return null;
    }
}
