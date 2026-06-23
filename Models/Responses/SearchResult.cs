using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Responses;

public class SearchResult
{
    public string Query { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / Math.Max(PageSize, 1));
    public List<SearchHit> Hits { get; set; } = new();
    public List<SearchFacet> Facets { get; set; } = new();
}

public class SearchHit
{
    public Guid Id { get; set; }
    public SearchableEntityType EntityType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Highlight { get; set; }
    public Dictionary<string, object> Fields { get; set; } = new();
    public DateTime CreatedDate { get; set; }
}

public class SearchFacet
{
    public SearchableEntityType EntityType { get; set; }
    public int Count { get; set; }
    public string Label { get; set; } = string.Empty;
}
