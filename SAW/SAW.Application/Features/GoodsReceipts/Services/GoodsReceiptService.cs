using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Interfaces;
using SAW.Application.Repositories;

namespace SAW.Application.Features.GoodsReceipts.Services;

public sealed class GoodsReceiptService(IGoodsReceiptQueryRepository queryRepository,
    IGoodsReceiptRepository repository) : IGoodsReceiptService
{
    public Task<GoodsReceiptPage<GoodsReceiptDetail>> SearchAsync(GoodsReceiptQuery query, CancellationToken ct)
    {
        ValidatePage(query.Page, query.PageSize);
        ValidateSearch(query.Search);
        if (query.SupplierId is <= 0 || query.WarehouseLocationId is <= 0 || query.FromDate > query.ToDate
            || query.ToDate == DateOnly.MaxValue
            || query.Status is not (null or "" or "DRAFT" or "COMMITTED")
            || query.SortBy is not (null or "receivedAtDesc" or "receivedAtAsc" or "idDesc" or "idAsc"))
            throw new BadRequestException("Bộ lọc, khoảng ngày hoặc cách sắp xếp không hợp lệ.");
        return queryRepository.SearchAsync(query with { Search = query.Search?.Trim(), SortBy = query.SortBy ?? "receivedAtDesc" }, ct);
    }

    public async Task<GoodsReceiptDetail> GetAsync(long id, CancellationToken ct)
    {
        if (id <= 0) throw new BadRequestException("Mã phiếu không hợp lệ.");
        return await queryRepository.GetAsync(id, ct) ?? throw new NotFoundException("Không tìm thấy phiếu nhập kho.");
    }

    public Task<GoodsReceiptFilters> FiltersAsync(CancellationToken ct) => queryRepository.FiltersAsync(ct);
    public Task<GoodsReceiptPage<GoodsReceiptBatch>> EligibleAsync(int? supplierId, string? search, int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize); ValidateSearch(search);
        if (supplierId is <= 0) throw new BadRequestException("Nhà cung cấp không hợp lệ.");
        return queryRepository.EligibleAsync(supplierId, search?.Trim(), page, pageSize, ct);
    }

    public async Task<GoodsReceiptDetail> CreateAsync(CreateGoodsReceiptRequest request, int actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        // Receiving date is a Vietnam calendar date, persisted at midnight (no timezone conversion).
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        if (request.ProductBatchId <= 0 || request.WarehouseLocationId <= 0)
            throw new BadRequestException("Vui lòng chọn lô và vị trí kho hợp lệ.");
        if (request.ReceivedDate == default || request.ReceivedDate > today)
            throw new BadRequestException("Ngày nhận không hợp lệ hoặc ở tương lai.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000) throw new BadRequestException("Ghi chú tối đa 1000 ký tự.");
        var id = await repository.CreateAsync(request with { Note = note }, actorId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<GoodsReceiptDetail> UpdateDraftAsync(long id, UpdateGoodsReceiptDraftRequest request, int actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        if (id <= 0 || request.WarehouseLocationId <= 0) throw new BadRequestException("Phiếu hoặc vị trí kho không hợp lệ.");
        ValidateSnapshot(request.ExpectedSnapshot);
        if (request.ReceivedDate == default || request.ReceivedDate > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)))
            throw new BadRequestException("Ngày nhận không hợp lệ hoặc ở tương lai.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000) throw new BadRequestException("Ghi chú tối đa 1000 ký tự.");
        await repository.UpdateDraftAsync(id, request with { Note = note }, actorId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<GoodsReceiptDetail> ConfirmAsync(long id, ConfirmGoodsReceiptRequest request, int actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new UnauthorizedAccessException();
        if (id <= 0) throw new BadRequestException("Mã phiếu không hợp lệ.");
        ValidateSnapshot(request.ExpectedSnapshot);
        await repository.ConfirmAsync(id, request.ExpectedSnapshot, actorId, ct);
        return await GetAsync(id, ct);
    }

    private static void ValidatePage(int page, int size)
    {
        if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue)
            throw new BadRequestException("Trang hoặc số dòng mỗi trang không hợp lệ.");
    }
    private static void ValidateSnapshot(GoodsReceiptSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.WarehouseLocationId <= 0 || snapshot.ReceivedAt == default || snapshot.Note?.Length > 1000)
            throw new BadRequestException("Thiếu phiên bản phiếu đã xem. Vui lòng tải lại phiếu.");
    }
    private static void ValidateSearch(string? text)
    {
        if (text?.Length > 100 || text?.Any(char.IsControl) == true)
            throw new BadRequestException("Từ khóa tìm kiếm không hợp lệ.");
    }
}
