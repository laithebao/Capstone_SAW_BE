using SAW.Application.Features.GoodsReceipts.Dtos;

namespace SAW.Application.Features.GoodsReceipts.Interfaces;

public interface IGoodsReceiptService
{
    Task<GoodsReceiptPage<GoodsReceiptDetail>> SearchAsync(GoodsReceiptQuery query, CancellationToken ct);
    Task<GoodsReceiptDetail> GetAsync(long id, CancellationToken ct);
    Task<GoodsReceiptFilters> FiltersAsync(CancellationToken ct);
    Task<GoodsReceiptPage<GoodsReceiptBatch>> EligibleAsync(int? supplierId, string? search, int page, int pageSize, CancellationToken ct);
    Task<GoodsReceiptDetail> CreateAsync(CreateGoodsReceiptRequest request, int actorId, CancellationToken ct);
    Task<GoodsReceiptDetail> UpdateDraftAsync(long id, UpdateGoodsReceiptDraftRequest request, int actorId, CancellationToken ct);
    Task<GoodsReceiptDetail> ConfirmAsync(long id, ConfirmGoodsReceiptRequest request, int actorId, CancellationToken ct);
}
