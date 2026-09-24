using System.Data;
using Dapper;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Stock;
using Jaftim.Domain.Entities.Stock;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// Reference implementation of the repository pattern for this codebase: one method per stored procedure, the
/// parameter list copied verbatim from the legacy Jaftim_Core StockRepository, audit params injected by the executor.
/// </summary>
public sealed class StockRepository(IDbExecutor db) : IStockRepository
{
    /// <summary>
    /// StockGetById is the visibility gate (reserved-stock scoping via ActionIds 626-628 - StockGetAll_New has none),
    /// but it projects only ~20 columns. The row returned is therefore fetched from StockGetAll_New filtered to the
    /// one id, so a single stock and a list row are the identical object.
    /// </summary>
    public async Task<StockListItem?> GetByIdAsync(long stockId, CancellationToken ct = default)
    {
        StockListItem? header = await db.QueryFirstOrDefaultAsync<StockListItem>(
            SpCall.Procedure("StockGetById").With("@StockId", stockId), ct);
        if (header is null) return null;

        PagedResult<StockListItem> full = await GetAllAsync(new StockListRequest { StockId = stockId, Page = 1, PageSize = 1 }, ct);
        return full.Items.FirstOrDefault() ?? header;
    }

    public Task<PagedResult<StockListItem>> GetAllAsync(StockListRequest request, CancellationToken ct = default)
    {
        // StockGetAll_New returns two result sets: the page rows, then the total count.
        string? sortOrder = request.SortBy is null ? null : $"{request.SortBy} {(request.SortDescending ? "desc" : "asc")}";

        SpCall call = SpCall.Procedure("StockGetAll_New")
            .With("@StockId", request.StockId?.ToString(), DbType.String, 50) // the SP takes a CSV of ids
            .With("@StockCode", request.StockCode, DbType.String, 50)
            .With("@MakeId", request.MakeId)
            .With("@ModelId", request.ModelId)
            .With("@PurchasedCountryId", request.PurchaseCountryId)
            .With("@LocationCountryId", request.LocationCountryId)
            .With("@DriveTypeId", request.DriveTypeId)
            .With("@FuelTypeId", request.FuelTypeId)
            .With("@SteeringId", request.SteeringId)
            .With("@SearchText", request.Search, DbType.String, 200)
            .With("@chassis", request.Chassis, DbType.String, 50)
            .With("@Stock_J_Status", request.JourneyStatus)
            .With("@SortOrder", sortOrder, DbType.String, 100)
            .With("@PageNumber", request.Page)
            .With("@PageSize", request.PageSize);

        return db.QueryMultipleAsync(call, async grid =>
        {
            IReadOnlyList<StockListItem> rows = (await grid.ReadAsync<StockListItem>()).AsList();
            int total = await grid.ReadFirstOrDefaultAsync<int>();
            return new PagedResult<StockListItem>(rows, request.Page, request.PageSize, total);
        }, ct);
    }

    // ----- detail sections -----

    public Task<IReadOnlyList<StockSectionValue>> GetSectionsAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockSectionValue>(SpCall.Procedure("Stock_GetSectionById").With("@Stock_Id", stockId), ct);

    public Task<StockWorkflow?> GetWorkflowAsync(long stockId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<StockWorkflow>(SpCall.Procedure("StockDetailWorkflowGetById").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockFlag>> GetFlagsAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockFlag>(SpCall.Procedure("GetStockFlagsByStockId").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockStatusDetail>> GetStatusDetailAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockStatusDetail>(SpCall.Procedure("Stock_GetStatusDetail").With("@Stock_Id", stockId), ct);

    public Task<IReadOnlyList<StockStatusHistoryItem>> GetStatusHistoryAsync(long stockId, long? baseStockStatusId, CancellationToken ct = default) =>
        db.QueryAsync<StockStatusHistoryItem>(
            SpCall.Procedure("Stock_GetStatusDetailHistory")
                .With("@Stock_Id", stockId)
                .With("@Base_StockStatus_Id", baseStockStatusId), ct);

    public Task<IReadOnlyList<StockFinancialLine>> GetFinancialsAsync(long stockId, int? financialTypeId, CancellationToken ct = default) =>
        db.QueryAsync<StockFinancialLine>(
            SpCall.Procedure("Stock_FinancialGetAllByStockId")
                .With("@Stock_Id", stockId)
                .With("@Stock_F_FinancialTypeId", financialTypeId), ct);

    public Task<IReadOnlyList<StockInternalNote>> GetInternalNotesAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockInternalNote>(SpCall.Procedure("GetStockInternalNote").With("@Stock_Id", stockId), ct);

    public Task<IReadOnlyList<StockFolder>> GetFoldersAsync(long stockId, StockFolderKind kind, CancellationToken ct = default)
    {
        string procedure = kind switch
        {
            StockFolderKind.Documents => "StockDocumentsGetByStockId",
            StockFolderKind.Images => "StockImagesGetByStockId",
            _ => "StockFolderGetByStockId",
        };
        return db.QueryAsync<StockFolder>(SpCall.Procedure(procedure).With("@Stock_Id", stockId), ct);
    }

    public Task<IReadOnlyList<StockFile>> GetFilesAsync(long folderId, CancellationToken ct = default) =>
        db.QueryAsync<StockFile>(SpCall.Procedure("StockFileGetByFolderId").With("@FolderId", folderId), ct);

    public Task<StockImage?> GetProfileImageAsync(long stockId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<StockImage>(SpCall.Procedure("StockImageGetByStockId").With("@StockId", stockId), ct);

    public Task<StockVideo?> GetVideoAsync(long stockId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<StockVideo>(SpCall.Procedure("GetStockVideoByStockId").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockReservationBid>> GetReservationBidsAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockReservationBid>(SpCall.Procedure("Stock_GetReservationBids").With("@StockId", stockId), ct);

    public async Task<string?> GetBossPriceHistoryJsonAsync(long stockId, CancellationToken ct = default)
    {
        // The procedure builds one JSON document and may return it split across several rows (FOR JSON behaviour).
        IReadOnlyList<string> chunks = await db.QueryAsync<string>(SpCall.Procedure("Stock_GetBossHistory").With("@Stock_Id", stockId), ct);
        return chunks.Count == 0 ? null : string.Concat(chunks);
    }

    // ----- history tabs -----

    public Task<IReadOnlyList<StockReservationHistoryItem>> GetReservationHistoryAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockReservationHistoryItem>(SpCall.Procedure("StockReservationHistoryGetAll").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockFinancialHistoryItem>> GetFinancialHistoryAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockFinancialHistoryItem>(SpCall.Procedure("StockFinancialHistoryGetAll").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockInspectionHistoryItem>> GetInspectionHistoryAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockInspectionHistoryItem>(SpCall.Procedure("StockInspectionHistory").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockPortLeaveHistoryItem>> GetPortLeaveHistoryAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockPortLeaveHistoryItem>(SpCall.Procedure("StockPortLeaveDetailHistoryGetByStockId").With("@StockId", stockId), ct);

    public Task<IReadOnlyList<StockBlHistoryItem>> GetBlHistoryAsync(long stockId, CancellationToken ct = default) =>
        db.QueryAsync<StockBlHistoryItem>(SpCall.Procedure("StockBlDetailHistoyGetByStockId").With("@StockId", stockId), ct);
}
