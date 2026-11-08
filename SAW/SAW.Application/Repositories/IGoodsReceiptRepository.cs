using SAW.Application.Features.GoodsReceipts.Dtos;

namespace SAW.Application.Repositories;

public interface IGoodsReceiptQueryRepository
{
    Task<GoodsReceiptPage<GoodsReceiptDetail>> SearchAsync(GoodsReceiptQuery query, CancellationToken ct);
    Task<GoodsReceiptDetail?> GetAsync(long id, CancellationToken ct);
    Task<GoodsReceiptFilters> FiltersAsync(CancellationToken ct);
    Task<GoodsReceiptPage<GoodsReceiptBatch>> EligibleAsync(int? supplierId, string? search, int page, int pageSize, CancellationToken ct);
}

public interface IGoodsReceiptRepository
{
    Task<long> CreateAsync(CreateGoodsReceiptRequest request, int actorId, CancellationToken ct);
    Task UpdateDraftAsync(long id, UpdateGoodsReceiptDraftRequest request, int actorId, CancellationToken ct);
    Task ConfirmAsync(long id, GoodsReceiptSnapshot expectedSnapshot, int actorId, CancellationToken ct);
}
