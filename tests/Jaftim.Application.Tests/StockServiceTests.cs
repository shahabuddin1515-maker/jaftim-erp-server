using Jaftim.Application.Common;
using Jaftim.Application.Modules.Stock;
using Jaftim.Domain.Entities.Stock;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class StockServiceTests
{
    [Fact]
    public async Task Sub_resource_reads_go_through_the_visibility_check()
    {
        var repo = new FakeStockRepository { Visible = false };
        var service = new StockService(repo, new StockListRequestValidator());

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetFlagsAsync(1));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetHistoryAsync(1, StockHistoryKind.Bl, null));

        Assert.Equal(0, repo.FlagsCalls);
    }

    [Fact]
    public async Task Files_of_a_folder_belonging_to_another_stock_are_refused()
    {
        var repo = new FakeStockRepository { Visible = true, Folders = [new StockFolder { Stock_Folder_Id = 10, Stock_Id = 1 }] };
        var service = new StockService(repo, new StockListRequestValidator());

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetFilesAsync(1, 99));
        Assert.Empty(await service.GetFilesAsync(1, 10));
    }

    [Fact]
    public async Task Sections_are_grouped_by_section_name()
    {
        var repo = new FakeStockRepository
        {
            Visible = true,
            Sections =
            [
                new StockSectionValue { SectionName = "Stock Detail", KeyName = "Make", KeyValue = "TOYOTA" },
                new StockSectionValue { SectionName = "Stock Detail", KeyName = "Model", KeyValue = "HILUX" },
                new StockSectionValue { SectionName = "Cost Details", KeyName = "FOB", KeyValue = "1" },
            ],
        };
        var service = new StockService(repo, new StockListRequestValidator());

        var grouped = await service.GetSectionsAsync(1);

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped["Stock Detail"].Count);
    }

    [Fact]
    public async Task List_request_is_validated()
    {
        var service = new StockService(new FakeStockRepository(), new StockListRequestValidator());
        await Assert.ThrowsAsync<ValidationException>(() => service.GetAllAsync(new StockListRequest { Page = 0 }));
    }

    private sealed class FakeStockRepository : IStockRepository
    {
        public bool Visible { get; init; }
        public List<StockFolder> Folders { get; init; } = [];
        public List<StockSectionValue> Sections { get; init; } = [];
        public int FlagsCalls { get; private set; }

        public Task<StockListItem?> GetByIdAsync(long stockId, CancellationToken ct = default) =>
            Task.FromResult(Visible ? new StockListItem { StockId = stockId } : null);
        public Task<PagedResult<StockListItem>> GetAllAsync(StockListRequest request, CancellationToken ct = default) => Task.FromResult(PagedResult<StockListItem>.Empty(request));
        public Task<IReadOnlyList<StockSectionValue>> GetSectionsAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockSectionValue>>(Sections);
        public Task<StockWorkflow?> GetWorkflowAsync(long stockId, CancellationToken ct = default) => Task.FromResult<StockWorkflow?>(null);
        public Task<IReadOnlyList<StockFlag>> GetFlagsAsync(long stockId, CancellationToken ct = default) { FlagsCalls++; return Task.FromResult<IReadOnlyList<StockFlag>>([]); }
        public Task<IReadOnlyList<StockStatusDetail>> GetStatusDetailAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockStatusDetail>>([]);
        public Task<IReadOnlyList<StockStatusHistoryItem>> GetStatusHistoryAsync(long stockId, long? baseStockStatusId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockStatusHistoryItem>>([]);
        public Task<IReadOnlyList<StockFinancialLine>> GetFinancialsAsync(long stockId, int? financialTypeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockFinancialLine>>([]);
        public Task<IReadOnlyList<StockInternalNote>> GetInternalNotesAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockInternalNote>>([]);
        public Task<IReadOnlyList<StockFolder>> GetFoldersAsync(long stockId, StockFolderKind kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockFolder>>(Folders);
        public Task<IReadOnlyList<StockFile>> GetFilesAsync(long folderId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockFile>>([]);
        public Task<StockImage?> GetProfileImageAsync(long stockId, CancellationToken ct = default) => Task.FromResult<StockImage?>(null);
        public Task<StockVideo?> GetVideoAsync(long stockId, CancellationToken ct = default) => Task.FromResult<StockVideo?>(null);
        public Task<IReadOnlyList<StockReservationBid>> GetReservationBidsAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockReservationBid>>([]);
        public Task<string?> GetBossPriceHistoryJsonAsync(long stockId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<StockReservationHistoryItem>> GetReservationHistoryAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockReservationHistoryItem>>([]);
        public Task<IReadOnlyList<StockFinancialHistoryItem>> GetFinancialHistoryAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockFinancialHistoryItem>>([]);
        public Task<IReadOnlyList<StockInspectionHistoryItem>> GetInspectionHistoryAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockInspectionHistoryItem>>([]);
        public Task<IReadOnlyList<StockPortLeaveHistoryItem>> GetPortLeaveHistoryAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockPortLeaveHistoryItem>>([]);
        public Task<IReadOnlyList<StockBlHistoryItem>> GetBlHistoryAsync(long stockId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockBlHistoryItem>>([]);
    }
}
