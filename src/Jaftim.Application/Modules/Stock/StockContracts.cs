using FluentValidation;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Stock;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Stock;

/// <summary>Filters accepted by EXEC StockGetAll_New (mirrors StockController.Index / StockService.StockGetAll).</summary>
public sealed record StockListRequest : PagedRequest
{
    public long? StockId { get; init; }
    public string? StockCode { get; init; }
    public long? MakeId { get; init; }
    public long? ModelId { get; init; }
    public long? PurchaseCountryId { get; init; }
    public long? LocationCountryId { get; init; }
    public long? DriveTypeId { get; init; }
    public long? FuelTypeId { get; init; }
    public long? SteeringId { get; init; }
    public string? Chassis { get; init; }
    /// <summary>Stock.Stock_C_J_Status (see StockJourneyStatus).</summary>
    public long? JourneyStatus { get; init; }
}

public sealed class StockListRequestValidator : AbstractValidator<StockListRequest>
{
    public StockListRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Chassis).MaximumLength(50);
        RuleFor(x => x.StockCode).MaximumLength(50);
    }
}

/// <summary>The history tabs of the stock detail screen (README section 8.5).</summary>
public enum StockHistoryKind
{
    Status,
    Reservation,
    Financial,
    Inspection,
    PortLeave,
    Bl,
}

/// <summary>
/// Stock reads. Row-level scoping (reserved-stock visibility, ActionIds 626-628) happens inside the procedures.
/// One method per stored procedure; parameter lists mirror the legacy StockRepository.
/// </summary>
public interface IStockRepository
{
    Task<StockListItem?> GetByIdAsync(long stockId, CancellationToken ct = default);
    Task<PagedResult<StockListItem>> GetAllAsync(StockListRequest request, CancellationToken ct = default);

    /// <summary>Stock_GetSectionById - every key/value of every detail section.</summary>
    Task<IReadOnlyList<StockSectionValue>> GetSectionsAsync(long stockId, CancellationToken ct = default);
    /// <summary>StockDetailWorkflowGetById.</summary>
    Task<StockWorkflow?> GetWorkflowAsync(long stockId, CancellationToken ct = default);
    /// <summary>GetStockFlagsByStockId.</summary>
    Task<IReadOnlyList<StockFlag>> GetFlagsAsync(long stockId, CancellationToken ct = default);
    /// <summary>Stock_GetStatusDetail.</summary>
    Task<IReadOnlyList<StockStatusDetail>> GetStatusDetailAsync(long stockId, CancellationToken ct = default);
    /// <summary>Stock_GetStatusDetailHistory (optionally for one Base_StockStatus_Id).</summary>
    Task<IReadOnlyList<StockStatusHistoryItem>> GetStatusHistoryAsync(long stockId, long? baseStockStatusId, CancellationToken ct = default);
    /// <summary>Stock_FinancialGetAllByStockId.</summary>
    Task<IReadOnlyList<StockFinancialLine>> GetFinancialsAsync(long stockId, int? financialTypeId, CancellationToken ct = default);
    /// <summary>GetStockInternalNote.</summary>
    Task<IReadOnlyList<StockInternalNote>> GetInternalNotesAsync(long stockId, CancellationToken ct = default);
    /// <summary>StockFolderGetByStockId (all) / StockDocumentsGetByStockId / StockImagesGetByStockId.</summary>
    Task<IReadOnlyList<StockFolder>> GetFoldersAsync(long stockId, StockFolderKind kind, CancellationToken ct = default);
    /// <summary>StockFileGetByFolderId.</summary>
    Task<IReadOnlyList<StockFile>> GetFilesAsync(long folderId, CancellationToken ct = default);
    /// <summary>StockImageGetByStockId - the profile image.</summary>
    Task<StockImage?> GetProfileImageAsync(long stockId, CancellationToken ct = default);
    /// <summary>GetStockVideoByStockId.</summary>
    Task<StockVideo?> GetVideoAsync(long stockId, CancellationToken ct = default);
    /// <summary>Stock_GetReservationBids.</summary>
    Task<IReadOnlyList<StockReservationBid>> GetReservationBidsAsync(long stockId, CancellationToken ct = default);
    /// <summary>Stock_GetBossHistory - returns a JSON document string built by the procedure.</summary>
    Task<string?> GetBossPriceHistoryJsonAsync(long stockId, CancellationToken ct = default);

    Task<IReadOnlyList<StockReservationHistoryItem>> GetReservationHistoryAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockFinancialHistoryItem>> GetFinancialHistoryAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockInspectionHistoryItem>> GetInspectionHistoryAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockPortLeaveHistoryItem>> GetPortLeaveHistoryAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockBlHistoryItem>> GetBlHistoryAsync(long stockId, CancellationToken ct = default);
}

public enum StockFolderKind
{
    All,
    Documents,
    Images,
}

public interface IStockService
{
    Task<StockListItem> GetByIdAsync(long stockId, CancellationToken ct = default);
    Task<PagedResult<StockListItem>> GetAllAsync(StockListRequest request, CancellationToken ct = default);
    /// <summary>Sections grouped by SectionName, as the detail tabs consume them.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StockSectionValue>>> GetSectionsAsync(long stockId, CancellationToken ct = default);
    Task<StockWorkflow> GetWorkflowAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockFlag>> GetFlagsAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockStatusDetail>> GetStatusDetailAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockFinancialLine>> GetFinancialsAsync(long stockId, int? financialTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<StockInternalNote>> GetInternalNotesAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockFolder>> GetFoldersAsync(long stockId, StockFolderKind kind, CancellationToken ct = default);
    Task<IReadOnlyList<StockFile>> GetFilesAsync(long stockId, long folderId, CancellationToken ct = default);
    Task<StockImage?> GetProfileImageAsync(long stockId, CancellationToken ct = default);
    Task<StockVideo?> GetVideoAsync(long stockId, CancellationToken ct = default);
    Task<IReadOnlyList<StockReservationBid>> GetReservationBidsAsync(long stockId, CancellationToken ct = default);
    Task<string?> GetBossPriceHistoryJsonAsync(long stockId, CancellationToken ct = default);
    /// <summary>One entry point for the six history tabs; the result element type depends on <paramref name="kind"/>.</summary>
    Task<object> GetHistoryAsync(long stockId, StockHistoryKind kind, long? baseStockStatusId, CancellationToken ct = default);
}

public sealed class StockService(IStockRepository repository, IValidator<StockListRequest> listValidator) : IStockService
{
    public async Task<StockListItem> GetByIdAsync(long stockId, CancellationToken ct = default) =>
        await repository.GetByIdAsync(stockId, ct) ?? throw new NotFoundException("Stock", stockId);

    public async Task<PagedResult<StockListItem>> GetAllAsync(StockListRequest request, CancellationToken ct = default)
    {
        await listValidator.ValidateAndThrowAppAsync(request, ct);
        return await repository.GetAllAsync(request, ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StockSectionValue>>> GetSectionsAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        IReadOnlyList<StockSectionValue> rows = await repository.GetSectionsAsync(stockId, ct);
        return rows
            .GroupBy(r => r.SectionName ?? string.Empty)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StockSectionValue>)g.ToList());
    }

    public async Task<StockWorkflow> GetWorkflowAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetWorkflowAsync(stockId, ct) ?? new StockWorkflow();
    }

    public async Task<IReadOnlyList<StockFlag>> GetFlagsAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetFlagsAsync(stockId, ct);
    }

    public async Task<IReadOnlyList<StockStatusDetail>> GetStatusDetailAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetStatusDetailAsync(stockId, ct);
    }

    public async Task<IReadOnlyList<StockFinancialLine>> GetFinancialsAsync(long stockId, int? financialTypeId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetFinancialsAsync(stockId, financialTypeId, ct);
    }

    public async Task<IReadOnlyList<StockInternalNote>> GetInternalNotesAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetInternalNotesAsync(stockId, ct);
    }

    public async Task<IReadOnlyList<StockFolder>> GetFoldersAsync(long stockId, StockFolderKind kind, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetFoldersAsync(stockId, kind, ct);
    }

    public async Task<IReadOnlyList<StockFile>> GetFilesAsync(long stockId, long folderId, CancellationToken ct = default)
    {
        // The legacy action took only a folder id; the stock id in the route lets us keep the visibility check and
        // refuse a folder that belongs to another stock.
        IReadOnlyList<StockFolder> folders = await GetFoldersAsync(stockId, StockFolderKind.All, ct);
        if (folders.All(f => f.Stock_Folder_Id != folderId))
            throw new NotFoundException("Stock folder", folderId);
        return await repository.GetFilesAsync(folderId, ct);
    }

    public async Task<StockImage?> GetProfileImageAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetProfileImageAsync(stockId, ct);
    }

    public async Task<StockVideo?> GetVideoAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetVideoAsync(stockId, ct);
    }

    public async Task<IReadOnlyList<StockReservationBid>> GetReservationBidsAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetReservationBidsAsync(stockId, ct);
    }

    public async Task<string?> GetBossPriceHistoryJsonAsync(long stockId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return await repository.GetBossPriceHistoryJsonAsync(stockId, ct);
    }

    public async Task<object> GetHistoryAsync(long stockId, StockHistoryKind kind, long? baseStockStatusId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(stockId, ct);
        return kind switch
        {
            StockHistoryKind.Status => await repository.GetStatusHistoryAsync(stockId, baseStockStatusId, ct),
            StockHistoryKind.Reservation => await repository.GetReservationHistoryAsync(stockId, ct),
            StockHistoryKind.Financial => await repository.GetFinancialHistoryAsync(stockId, ct),
            StockHistoryKind.Inspection => await repository.GetInspectionHistoryAsync(stockId, ct),
            StockHistoryKind.PortLeave => await repository.GetPortLeaveHistoryAsync(stockId, ct),
            StockHistoryKind.Bl => await repository.GetBlHistoryAsync(stockId, ct),
            _ => throw new BusinessRuleException($"Unknown history kind {kind}."),
        };
    }

    /// <summary>
    /// The sub-entity procedures do not apply the reserved-stock visibility rule themselves (the legacy screen was
    /// only reachable through StockDetail, which did). Going through StockGetById first keeps that guarantee.
    /// </summary>
    private async Task EnsureVisibleAsync(long stockId, CancellationToken ct) =>
        _ = await repository.GetByIdAsync(stockId, ct) ?? throw new NotFoundException("Stock", stockId);
}
