using System.Data;
using Dapper;
using Jaftim.Application.Common;

namespace Jaftim.Infrastructure.Data;

/// <summary>
/// The paged-list procedure contract (database/v2/010_PagedLists.sql): the procedure takes @Search, @SortBy,
/// @SortDescending, @PageNumber, @PageSize and returns the page rows, then TotalRecords. Search, sort whitelisting
/// and the page-size clamp all happen in SQL; this only passes the request through and reads the two result sets.
/// </summary>
public static class PagedQuery
{
    /// <summary>Row offset for the older procedures that take @Skip/@Take (AuditLog_GetByEntity, Notification_GetByUser).</summary>
    public static int Offset(this PagedRequest request) => (Math.Max(request.Page, 1) - 1) * request.PageSize;

    public static SpCall WithPaging(this SpCall call, PagedRequest request) =>
        call.With("@Search", request.Search, DbType.String, 200)
            .With("@SortBy", request.SortBy, DbType.String, 50)
            .With("@SortDescending", request.SortDescending)
            .With("@PageNumber", request.Page)
            .With("@PageSize", request.PageSize);

    public static Task<PagedResult<T>> QueryPagedAsync<T>(this IDbExecutor db, SpCall call, PagedRequest request, CancellationToken ct = default) =>
        db.QueryMultipleAsync(call.WithPaging(request), async grid =>
        {
            IReadOnlyList<T> rows = (await grid.ReadAsync<T>()).AsList();
            int total = await grid.ReadFirstOrDefaultAsync<int>();
            return new PagedResult<T>(rows, Math.Max(request.Page, 1), request.PageSize, total);
        }, ct);
}
