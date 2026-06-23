using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public SearchableEntityType EntityTypes { get; set; } = SearchableEntityType.All;
    public string? ChainId { get; set; }
    public string? AssetType { get; set; }
    public Guid? AvatarId { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public string SortBy { get; set; } = "CreatedDate";
    public bool SortDescending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
