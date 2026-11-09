
namespace MarketAPI.Options;
public class ListParams
{
    public string? SearchString { get; set; }
    public string? SearchFields { get; set; } 
    public DateTime? Date { get; set; } = null;
    public int? Number { get; set; } = null;
    public int? PageNumber { get; set; } = null;
    public int? PageSize { get; set; } = null;
    public int? Total { get; set; } = null;
    public long? IncludeId { get; set; } = null;
    public int? ParentId { get; set; } = null;
    public bool IsActive { get; set; } = true;
    public string? OrderDirection { get; set; } = "asc";
    public string? OrderBy { get; set; } = string.Empty;
    public List<int> Group { get; set; } = new();
}
