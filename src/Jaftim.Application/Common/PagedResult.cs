namespace Jaftim.Application.Common;

/// <summary>Standard paging input. Legacy screens used page/pageSize + a free-text search + "Column dir" sort.</summary>
public record PagedRequest
{
    private const int MaxPageSize = 200;
    private readonly int _pageSize = 10;

    public int Page { get; init; } = 1;
    public int PageSize { get => _pageSize; init => _pageSize = Math.Clamp(value, 1, MaxPageSize); }
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }

    public int Skip => (Math.Max(Page, 1) - 1) * PageSize;
}

/// <summary>Standard paged output. The API serialises this as-is inside the response envelope.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalRecords)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalRecords / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;

    public static PagedResult<T> Empty(PagedRequest request) => new([], request.Page, request.PageSize, 0);
}
