namespace SAW.Application.Features.WarehouseInventory;

public sealed class WarehouseInventoryService(IWarehouseInventoryRepository repository) : IWarehouseInventoryService
{
    public Task<WarehouseInventoryLevelChart> GetLevelChartAsync(CancellationToken ct) =>
        repository.GetLevelChartAsync(ct);

    public Task<WarehouseCapacityChart> GetCapacityChartAsync(CancellationToken ct) =>
        repository.GetCapacityChartAsync(ct);

    public Task<ProductQualityDistributionChart> GetQualityDistributionChartAsync(CancellationToken ct) =>
        repository.GetQualityDistributionChartAsync(ct);
    public Task<WarehouseDistributorOrderPage> GetDistributorOrdersAsync(string? status, int page, int pageSize, CancellationToken ct) => repository.GetDistributorOrdersAsync(status, page, pageSize, ct);
    public Task<WarehouseDistributorOrderDetail> GetDistributorOrderAsync(long id, CancellationToken ct) => repository.GetDistributorOrderAsync(id, ct);
    public Task<WarehouseDistributorOrderDetail> ApproveDistributorOrderAsync(int actorAccountId, long id, CancellationToken ct) => repository.ApproveDistributorOrderAsync(actorAccountId, id, ct);
}
