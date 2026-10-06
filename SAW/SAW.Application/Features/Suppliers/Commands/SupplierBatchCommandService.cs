using SAW.Application.Exceptions;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Application.Features.Suppliers.Commands;

public class SupplierBatchCommandService(IProductBatchRepository batches, ISupplierRepository suppliers) : ISupplierBatchCommandService
{
    public Task<SupplierBatchListResponse> GetDeclaredBatchesAsync(int accountId, GetSupplierBatchesQueryRequest request, CancellationToken ct = default) =>
        batches.GetBatchesBySupplierAccountIdAsync(accountId, request, ct);

    private async Task<Supplier> GetSupplier(int accountId, CancellationToken ct)
    {
        var supplier = await suppliers.GetEntityByAccountIdAsync(accountId, ct)
            ?? throw new KeyNotFoundException("Vui lòng khai báo hồ sơ nhà cung cấp trước.");
        if (supplier.ProfileStatus != "ACTIVE") throw new UnauthorizedAccessException("Hồ sơ nhà cung cấp chưa hoạt động.");
        return supplier;
    }

    private async Task<decimal> Validate(int supplierId, DeclareProductBatchRequest request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var weight = SupplierBatchValidation.ValidateAndCalculateWeight(request, today);
        if (!await batches.IsCropTypeRegisteredForSupplierAsync(supplierId, request.CropTypeId, ct))
            throw new ArgumentException("Nông sản chưa được đăng ký hoặc không còn hoạt động.");
        if (!await batches.IsGrowingAreaRegisteredForSupplierAsync(supplierId, request.GrowingAreaId, ct))
            throw new ArgumentException("Vùng trồng không thuộc nhà cung cấp hiện tại.");
        return weight;
    }

    public async Task<SupplierBatchItemResponse> DeclareBatchAsync(int accountId, DeclareProductBatchRequest request, CancellationToken ct = default)
    {
        var supplier = await GetSupplier(accountId, ct);
        var weight = await Validate(supplier.SupplierId, request, ct);
        var batch = new ProductBatch
        {
            BatchCode = await batches.GenerateBatchCodeAsync(ct), SupplierId = supplier.SupplierId,
            BatchStatus = "SUBMITTED", CreatedAt = DateTime.UtcNow
        };
        Apply(batch, request, weight);
        var history = History(batch, accountId, null, "SUBMITTED", "Khai báo lô hàng.");
        await batches.AddProductBatchAsync(batch, history, request.EvidenceDocumentUrls, ct);
        return Item(batch);
    }

    public async Task<SupplierBatchItemResponse> UpdateDeclaredBatchAsync(long id, int accountId, UpdateProductBatchRequest request, CancellationToken ct = default)
    {
        var supplier = await GetSupplier(accountId, ct);
        var batch = await OwnedBatch(id, supplier.SupplierId, ct);
        if (batch.BatchStatus != "SUBMITTED") throw new ConflictException("Lô hàng đã được xử lý. Vui lòng tải lại dữ liệu.");
        if (request.ExpectedCreatedAt is null) throw new ArgumentException("Thiếu phiên bản lô hàng. Vui lòng tải lại dữ liệu.");
        var weight = await Validate(supplier.SupplierId, request, ct);
        Apply(batch, request, weight);
        await batches.UpdateProductBatchAsync(batch,
            History(batch, accountId, "SUBMITTED", "SUBMITTED", "Cập nhật khai báo lô hàng."),
            request.ExpectedCreatedAt.Value, request.ExpectedUpdatedAt, request.EvidenceDocumentUrls, ct);
        return Item(batch);
    }

    public async Task CancelBatchAsync(long id, int accountId, CancelSupplierBatchRequest request, CancellationToken ct = default)
    {
        var supplier = await GetSupplier(accountId, ct);
        var batch = await OwnedBatch(id, supplier.SupplierId, ct);
        if (batch.BatchStatus != "SUBMITTED") throw new ConflictException("Lô hàng đã được xử lý nên không thể hủy. Vui lòng tải lại dữ liệu.");
        if (request.ExpectedCreatedAt is null) throw new ArgumentException("Thiếu phiên bản lô hàng. Vui lòng tải lại dữ liệu.");
        batch.BatchStatus = "CANCELLED";
        await batches.UpdateProductBatchAsync(batch,
            History(batch, accountId, "SUBMITTED", "CANCELLED", "Nhà cung cấp hủy lô hàng."),
            request.ExpectedCreatedAt.Value, request.ExpectedUpdatedAt, null, ct);
    }

    private async Task<ProductBatch> OwnedBatch(long id, int supplierId, CancellationToken ct)
    {
        var batch = await batches.GetBatchByIdAsync(id, ct) ?? throw new KeyNotFoundException("Không tìm thấy lô hàng.");
        if (batch.SupplierId != supplierId) throw new UnauthorizedAccessException("Bạn không có quyền truy cập lô hàng này.");
        return batch;
    }

    public async Task<SupplierBatchStatusResponse> GetBatchStatusDetailAsync(long id, int accountId, CancellationToken ct = default)
    {
        var supplier = await GetSupplier(accountId, ct);
        var batch = await batches.GetBatchStatusDetailByIdAsync(id, ct) ?? throw new KeyNotFoundException("Không tìm thấy lô hàng.");
        if (batch.SupplierId != supplier.SupplierId) throw new UnauthorizedAccessException("Bạn không có quyền xem lô hàng này.");
        var history = await batches.GetBatchStatusHistoryAsync(id, ct);
        // Hydrate the read-only presentation with the same history evidence used by the list query.
        batch.StatusHistories = history;
        var visibleStatus = SupplierBatchPresentation.Get(batch).Status;
        var warehousedAt = batch.GoodsReceipts.Where(g => g.ReceiptStatus == "COMMITTED")
            .Select(g => g.CommittedAt).Min();
        warehousedAt ??= history.Where(h => h.NewStatus == "IN_STOCK")
            .Select(h => (DateTime?)h.ChangedAt).Min();
        // Supplier progress ends at warehouse entry, including when a later inspection exists.
        var inspections = batch.QcInspections.Where(q => warehousedAt == null || q.StartedAt <= warehousedAt.Value);
        var latestInspection = inspections.OrderByDescending(q => q.StartedAt)
            .ThenByDescending(q => q.QcInspectionId).FirstOrDefault();
        var qc = inspections.Where(q => q.InspectionStatus == "COMPLETED" &&
                (warehousedAt == null || q.CompletedAt <= warehousedAt.Value))
            .OrderByDescending(q => q.CompletedAt ?? q.StartedAt).ThenByDescending(q => q.QcInspectionId).FirstOrDefault();
        return new SupplierBatchStatusResponse
        {
            BatchId = id, BatchCode = batch.BatchCode, ProductName = batch.ProductName,
            CropTypeId = batch.CropTypeId, GrowingAreaId = batch.GrowingAreaId,
            CropTypeName = batch.CropType?.CropName ?? "", AreaName = batch.GrowingArea?.AreaName ?? "",
            Province = batch.GrowingArea?.Province ?? "", District = batch.GrowingArea?.District ?? "", Ward = batch.GrowingArea?.Ward ?? "",
            HarvestDate = batch.HarvestDate, DeclaredQuantity = batch.DeclaredQuantity, Unit = batch.Unit,
            WeightInKg = batch.WeightInKg, VerifiedQuantity = batch.VerifiedQuantity, VerifiedWeightInKg = batch.VerifiedWeightInKg,
            PackagingType = batch.PackagingType, PackageCount = batch.PackageCount, PackageUnitWeightKg = batch.PackageUnitWeightKg,
            ExpectedMinTempC = batch.ExpectedMinTempC, ExpectedMaxTempC = batch.ExpectedMaxTempC,
            ExpectedMinHumidityPct = batch.ExpectedMinHumidityPct, ExpectedMaxHumidityPct = batch.ExpectedMaxHumidityPct,
            ExpiryDate = batch.ExpiryDate, ExpectedDeliveryDate = batch.ExpectedDeliveryDate,
            ReceivedQuantity = await batches.GetCommittedReceivedQuantityAsync(id, ct),
            CurrentStatus = visibleStatus, StatusDisplayName = SupplierBatchStatuses.Label(visibleStatus),
            QcResult = qc?.QcResult, QualityGrade = visibleStatus == "IN_STOCK" ? qc?.QualityGrade : batch.QualityGrade ?? qc?.QualityGrade,
            QcInspectionStatus = latestInspection?.InspectionStatus, QcCompletedAt = qc?.CompletedAt,
            WarehousedAt = warehousedAt,
            RejectionReason = visibleStatus == "IN_STOCK" ? null : batch.RejectionReason ?? (qc?.QcResult is "FAIL" or "FAILED" ? qc.Note : null),
            SupplierNote = batch.Note, WarehouseNote = batch.ReceivingNote,
            CreatedAt = batch.CreatedAt, UpdatedAt = batch.UpdatedAt,
            Documents = await batches.GetDocumentsAsync(id, ct),
            StatusHistory = SupplierBatchPresentation.VisibleHistory(history, warehousedAt).Select(h => new BatchStatusHistoryDto
            {
                OldStatus = h.OldStatus ?? "", NewStatus = h.NewStatus,
                ChangeReason = h.ChangeReason, ChangedAt = h.ChangedAt,
                ChangedBy = h.ChangedByAccount?.FullName
            }).ToList()
        };
    }

    private static void Apply(ProductBatch b, DeclareProductBatchRequest r, decimal weight)
    {
        b.CropTypeId = r.CropTypeId; b.GrowingAreaId = r.GrowingAreaId; b.ProductName = r.ProductName.Trim();
        b.HarvestDate = r.HarvestDate; b.DeclaredQuantity = r.DeclaredQuantity; b.Unit = r.Unit.Trim(); b.WeightInKg = weight;
        b.PackagingType = Clean(r.PackagingType); b.PackageCount = r.PackageCount; b.PackageUnitWeightKg = r.PackageUnitWeightKg;
        b.ExpectedMinTempC = r.ExpectedMinTempC; b.ExpectedMaxTempC = r.ExpectedMaxTempC;
        b.ExpectedMinHumidityPct = r.ExpectedMinHumidityPct; b.ExpectedMaxHumidityPct = r.ExpectedMaxHumidityPct;
        b.ExpectedDeliveryDate = r.ExpectedDeliveryDate; b.ExpiryDate = r.ExpiryDate; b.Note = Clean(r.Note);
    }
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static SupplierBatchItemResponse Item(ProductBatch b) => new()
    {
        BatchId = b.ProductBatchId, BatchCode = b.BatchCode, ProductName = b.ProductName,
        QuantityInTons = b.WeightInKg / 1000m, SubmittedDate = b.CreatedAt,
        Status = b.BatchStatus, StatusDisplayName = SupplierBatchStatuses.Label(b.BatchStatus)
    };
    private static BatchStatusHistory History(ProductBatch b, int accountId, string? oldStatus, string newStatus, string reason) => new()
    {
        ProductBatchId = b.ProductBatchId, OldStatus = oldStatus, NewStatus = newStatus,
        ChangedByAccountId = accountId, ChangeReason = reason, ChangedAt = DateTime.UtcNow
    };
}

public static class SupplierBatchStatuses
{
    public static readonly string[] Approved = ["APPROVED_FOR_STORAGE", "RECEIVED", "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "ISSUED"];
    public static readonly string[] All = ["PENDING_PREDECLARATION", "SUBMITTED", "PENDING_QC", "APPROVED_FOR_STORAGE", "QUARANTINE", "REJECTED", "RECEIVED", "IN_STOCK", "RESERVED", "PARTIALLY_ISSUED", "ISSUED", "CANCELLED"];
    public static string Label(string status) => status switch
    {
        "PENDING_PREDECLARATION" => "Chờ khai báo", "SUBMITTED" => "Chờ tiếp nhận", "PENDING_QC" => "Chờ kiểm định QC",
        "APPROVED_FOR_STORAGE" => "Đã duyệt nhập kho", "QUARANTINE" => "Cách ly", "REJECTED" => "Bị từ chối",
        "RECEIVED" => "Đã nhận hàng", "IN_STOCK" => "Đã nhập kho", "RESERVED" => "Đã giữ hàng",
        "PARTIALLY_ISSUED" => "Đã xuất một phần", "ISSUED" => "Đã xuất hết", "CANCELLED" => "Đã hủy", _ => status
    };
}
