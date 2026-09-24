using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Stock;
using Jaftim.Domain.Entities.Stock;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Reference module. Legacy: StockController.Index (list), StockDetail and its tabs (read side). Row-level visibility
/// of reserved stock (ActionIds 626-628) is enforced inside StockGetById using the caller's UserProfileId; every
/// sub-resource read goes through that check first (StockService.EnsureVisibleAsync).
/// Permission ids are the _CSS_### classes the legacy tabs/buttons carried (docs/MIGRATION_INVENTORY.md).
/// </summary>
public sealed class StocksController(IStockService stocks) : ApiControllerBase
{
    /// <summary>Paged stock list with the same filters as the legacy Stock/Index screen.</summary>
    [HttpGet]
    [HasPermission(Permissions.StockListing)]
    public async Task<ActionResult<ApiResponse<PagedResult<StockListItem>>>> GetAll([FromQuery] StockListRequest request, CancellationToken ct) =>
        Ok(await stocks.GetAllAsync(request, ct));

    /// <summary>Stock header (StockGetById).</summary>
    [HttpGet("{stockId:long}")]
    [HasPermission(Permissions.StockDetail)]
    public async Task<ActionResult<ApiResponse<StockListItem>>> GetById(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetByIdAsync(stockId, ct));

    /// <summary>All detail-tab key/values grouped by section (Stock_GetSectionById).</summary>
    [HttpGet("{stockId:long}/sections")]
    [HasPermission(Permissions.StockSections)]
    public async Task<ActionResult<ApiResponse<IReadOnlyDictionary<string, IReadOnlyList<StockSectionValue>>>>> GetSections(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetSectionsAsync(stockId, ct));

    /// <summary>Release criteria and pending shipping request (StockDetailWorkflowGetById).</summary>
    [HttpGet("{stockId:long}/workflow")]
    [HasPermission(Permissions.StockDetail)]
    public async Task<ActionResult<ApiResponse<StockWorkflow>>> GetWorkflow(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetWorkflowAsync(stockId, ct));

    /// <summary>Stock_Info flags: Modified/Featured/Special Offer/Discounted/Open for Bid/Web Display/Coming Soon.</summary>
    [HttpGet("{stockId:long}/flags")]
    [HasPermission(Permissions.StockTabOverview)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockFlag>>>> GetFlags(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetFlagsAsync(stockId, ct));

    /// <summary>Current value of every check flag (Stock_GetStatusDetail) - the Status tab.</summary>
    [HttpGet("{stockId:long}/status")]
    [HasPermission(Permissions.StockTabStatus)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockStatusDetail>>>> GetStatus(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetStatusDetailAsync(stockId, ct));

    /// <summary>Financial lines (Stock_FinancialGetAllByStockId), optionally filtered by Base_FinancialType id.</summary>
    [HttpGet("{stockId:long}/financials")]
    [HasPermission(Permissions.StockFinancials)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockFinancialLine>>>> GetFinancials(long stockId, [FromQuery] int? financialTypeId, CancellationToken ct) =>
        Ok(await stocks.GetFinancialsAsync(stockId, financialTypeId, ct));

    /// <summary>Internal notes tab.</summary>
    [HttpGet("{stockId:long}/notes")]
    [HasPermission(Permissions.StockNote)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockInternalNote>>>> GetNotes(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetInternalNotesAsync(stockId, ct));

    /// <summary>Folder tree. kind = All | Documents | Images (StockFolderGetByStockId / StockDocumentsGetByStockId / StockImagesGetByStockId).</summary>
    [HttpGet("{stockId:long}/folders")]
    [HasPermission(Permissions.StockFile)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockFolder>>>> GetFolders(long stockId, [FromQuery] StockFolderKind kind = StockFolderKind.All, CancellationToken ct = default) =>
        Ok(await stocks.GetFoldersAsync(stockId, kind, ct));

    /// <summary>Files in one folder (StockFileGetByFolderId). The folder must belong to the stock.</summary>
    [HttpGet("{stockId:long}/folders/{folderId:long}/files")]
    [HasPermission(Permissions.StockFile)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockFile>>>> GetFiles(long stockId, long folderId, CancellationToken ct) =>
        Ok(await stocks.GetFilesAsync(stockId, folderId, ct));

    /// <summary>The profile image (StockImageGetByStockId); null when none is set.</summary>
    [HttpGet("{stockId:long}/profile-image")]
    [HasPermission(Permissions.StockTabImages)]
    public async Task<ActionResult<ApiResponse<StockImage?>>> GetProfileImage(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetProfileImageAsync(stockId, ct));

    /// <summary>The stock video (GetStockVideoByStockId); null when none.</summary>
    [HttpGet("{stockId:long}/video")]
    [HasPermission(Permissions.StockTabImages)]
    public async Task<ActionResult<ApiResponse<StockVideo?>>> GetVideo(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetVideoAsync(stockId, ct));

    /// <summary>Reservation bids grid (Stock_GetReservationBids).</summary>
    [HttpGet("{stockId:long}/reservation-bids")]
    [HasPermission(Permissions.StockTabReservationBids)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockReservationBid>>>> GetReservationBids(long stockId, CancellationToken ct) =>
        Ok(await stocks.GetReservationBidsAsync(stockId, ct));

    /// <summary>Boss/display price snapshot history (Stock_GetBossHistory) - a JSON document produced by the procedure.</summary>
    [HttpGet("{stockId:long}/history/price")]
    [HasPermission(Permissions.StockActivity)]
    [Produces("application/json")]
    public async Task<IActionResult> GetPriceHistory(long stockId, CancellationToken ct)
    {
        string? json = await stocks.GetBossPriceHistoryJsonAsync(stockId, ct);
        return Content(json ?? "[]", "application/json");
    }

    /// <summary>History tabs: status | reservation | financial | inspection | portleave | bl. Status accepts ?baseStockStatusId=.</summary>
    [HttpGet("{stockId:long}/history/{kind}")]
    [HasPermission(Permissions.StockActivity)]
    public async Task<ActionResult<ApiResponse<object>>> GetHistory(long stockId, StockHistoryKind kind, [FromQuery] long? baseStockStatusId, CancellationToken ct) =>
        Ok(await stocks.GetHistoryAsync(stockId, kind, baseStockStatusId, ct));
}
