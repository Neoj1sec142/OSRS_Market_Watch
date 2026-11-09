using MarketAPI.Options;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Extensions.Query;

public static class QueryablePagingExtensions
{
    public static void NormalizePaging(this ListParams p, int defaultPageSize = 50, int maxPageSize = 500)
    {
        // PageSize
        var size = p.PageSize.GetValueOrDefault(defaultPageSize);
        if (size <= 0) size = defaultPageSize;
        if (size > maxPageSize) size = maxPageSize;
        p.PageSize = size;

        // PageNumber
        var page = p.PageNumber.GetValueOrDefault(1);
        if (page <= 0) page = 1;
        p.PageNumber = page;
    }

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        ListParams listParams,
        CancellationToken ct = default)
    {
        // listParams.NormalizePaging();
        var hasPaging =
            listParams.PageNumber.GetValueOrDefault() > 0 &&
            listParams.PageSize.GetValueOrDefault() > 0;

        var pageNumber = hasPaging ? listParams.PageNumber!.Value : 1;
        var pageSize   = hasPaging ? listParams.PageSize!.Value : int.MaxValue;

        var total = await query.CountAsync(ct);
        listParams.Total = total;

        var itemsQuery = query;

        if (hasPaging)
        {
            itemsQuery = itemsQuery
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize);
        }

        var items = await itemsQuery.ToListAsync(ct);

        var totalPages = !hasPaging
            ? (total == 0 ? 0 : 1)
            : (total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));

        return new PagedResult<T>
        {
            Items = items,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = totalPages,

            // echo back useful request context (optional but handy)
            ParentId = listParams.ParentId,
            IsActive = listParams.IsActive,
            SearchString = listParams.SearchString,
            OrderBy = listParams.OrderBy,
            OrderDirection = listParams.OrderDirection
        };
    }

    public static PagedResult<TDest> Map<TSource, TDest>(
        this PagedResult<TSource> source,
        Func<TSource, TDest> projector)
    {
        return new PagedResult<TDest>
        {
            Items = source.Items.Select(projector).ToList(),

            PageNumber = source.PageNumber,
            PageSize = source.PageSize,
            TotalCount = source.TotalCount,
            TotalPages = source.TotalPages,

            ParentId = source.ParentId,
            IsActive = source.IsActive,
            SearchString = source.SearchString,
            OrderBy = source.OrderBy,
            OrderDirection = source.OrderDirection
        };
    }

    public static IQueryable<T> ApplyPagingIfRequested<T>(this IQueryable<T> query, ListParams listParams, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (listParams is null) return query;

        if (!listParams.HasPaging())
            return query; // no Skip/Take => controller can ToListAsync()

        var (pageNumber, pageSize) = listParams.GetPagingOrThrow();

        // PageNumber is 1-based
        var skip = (pageNumber - 1) * pageSize;

        return query.Skip(skip).Take(pageSize);
    }

    public static bool HasPaging(this ListParams p)
    {
        if (p is null) return false;

        var page = p.PageNumber.GetValueOrDefault();
        var size = p.PageSize.GetValueOrDefault();

        return page > 0 && size > 0;
    }

    public static (int PageNumber, int PageSize) GetPagingOrThrow(this ListParams p)
    {
        if (!p.HasPaging())
            throw new InvalidOperationException("Paging not requested (PageNumber/PageSize must be > 0).");

        return (p.PageNumber!.Value, p.PageSize!.Value);
    }
}
