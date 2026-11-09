namespace MarketAPI.Options;

public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public int PageNumber { get; init; }
    public int PageSize { get; init; }

    public int TotalCount { get; init; }
    public int TotalPages { get; init; }

    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;

    public int? ParentId { get; init; }
    public bool? IsActive { get; init; }
    public string? SearchString { get; init; }
    public string? OrderBy { get; init; }
    public string? OrderDirection { get; init; }
}
