using System.Text.RegularExpressions;
using SAW.Application.Exceptions;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Application.Features.ProductBatches.Services;

public sealed class ProductBatchService(
    IProductBatchQueryRepository repository,
    IProductBatchVerificationRepository verificationRepository) : IProductBatchService
{
    private const string Submitted = "SUBMITTED";
    private const string PendingQc = "PENDING_QC";
    private const string Rejected = "REJECTED";
    public const string QcReceivingLockMessage = "Lô hàng đã được QC tiếp nhận. Không thể cập nhật thông tin kiểm nhận.";
    private const string NoLongerAvailable = "Product batch is no longer available for warehouse verification.";
    private static readonly HashSet<string> AllowedSorts =
        ["createdAtDesc", "createdAtAsc", "updatedAtDesc", "updatedAtAsc"];

    public async Task<ProductBatchListResponse> SearchAsync(ProductBatchQuery query, CancellationToken cancellationToken)
    {
        var batchCode = query.BatchCode?.Trim();
        var status = query.Status?.Trim().ToUpperInvariant();
        var sortBy = string.IsNullOrWhiteSpace(query.SortBy) ? "createdAtDesc" : query.SortBy.Trim();

        if (query.Page < 1 || query.PageSize is < 1 or > 100)
            throw new BadRequestException("Invalid page number or page size.");
        if (query.SupplierId is <= 0 || query.CropTypeId is <= 0 ||
            (batchCode is not null && (batchCode.Length > 50 || batchCode.Any(char.IsControl))) ||
            (status is not null && (status.Length > 40 || !Regex.IsMatch(status, "^[A-Z0-9_]*$"))))
            throw new BadRequestException("Invalid search or filter value.");
        if (!AllowedSorts.Contains(sortBy))
            throw new BadRequestException("Invalid sort value.");

        var normalized = query with
        {
            BatchCode = string.IsNullOrWhiteSpace(batchCode) ? null : batchCode,
            Status = string.IsNullOrWhiteSpace(status) ? null : status,
            SortBy = sortBy
        };
        var result = await repository.SearchAsync(normalized, cancellationToken);
        return new ProductBatchListResponse(result.Items.Select(MapList).ToList(),
            result.TotalCount, normalized.Page, normalized.PageSize);
    }

    public Task<ProductBatchFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken) =>
        repository.GetFilterOptionsAsync(cancellationToken);

    public async Task<ProductBatchDetail> GetAsync(long id, CancellationToken cancellationToken)
    {
        var batch = await repository.GetWarehouseByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Product batch not found.");
        var hasInspection = await repository.HasQcInspectionAsync(id, cancellationToken);
        var lockReason = hasInspection ? QcReceivingLockMessage
            : batch.BatchStatus != PendingQc ? "Chỉ có thể cập nhật lô đang chờ kiểm định QC." : null;
        return new ProductBatchDetail(
            batch.ProductBatchId, batch.BatchCode, batch.ProductName,
            batch.SupplierId, batch.Supplier.SupplierName,
            batch.CropTypeId, batch.CropType.CropName, batch.CropType.CategoryName,
            batch.DeclaredQuantity, batch.Unit, batch.WeightInKg,
            batch.VerifiedQuantity, batch.VerifiedWeightInKg, batch.HarvestDate,
            batch.GrowingArea.AreaName, batch.ExpectedDeliveryDate, batch.ExpiryDate,
            batch.CreatedAt, batch.UpdatedAt, batch.BatchStatus, batch.Note,
            batch.PackagingType, batch.PackageCount, batch.PackageUnitWeightKg,
            batch.VerifiedPackagingType, batch.VerifiedPackageCount,
            batch.VerifiedPackageUnitWeightKg, batch.ReceivingNote, batch.RejectionReason,
            batch.ExpectedMinTempC, batch.ExpectedMaxTempC,
            batch.ExpectedMinHumidityPct, batch.ExpectedMaxHumidityPct,
            batch.ShelfLifeDaysSnapshot, lockReason is null, lockReason);
    }

    public Task<IReadOnlyList<ProductBatchFilterOption>> GetSubmittedSuppliersAsync(CancellationToken cancellationToken) =>
        repository.GetSubmittedSuppliersAsync(cancellationToken);

    public Task<IReadOnlyList<SubmittedDeclarationOption>> GetSubmittedBySupplierAsync(
        int supplierId, CancellationToken cancellationToken)
    {
        if (supplierId <= 0) throw new BadRequestException("Invalid supplier ID.");
        return repository.GetSubmittedBySupplierAsync(supplierId, cancellationToken);
    }

    public async Task<SubmittedDeclarationDetail> GetSubmittedDetailAsync(
        long id, int supplierId, CancellationToken cancellationToken)
    {
        var batch = await GetAvailableBatchAsync(id, supplierId, cancellationToken);
        return new SubmittedDeclarationDetail(
            batch.ProductBatchId, batch.BatchCode, batch.SupplierId, batch.Supplier.SupplierName,
            batch.CropTypeId, batch.CropType.CropName, batch.ProductName,
            batch.GrowingAreaId, batch.GrowingArea.AreaName, batch.HarvestDate,
            batch.DeclaredQuantity, batch.Unit, batch.WeightInKg,
            batch.PackagingType, batch.PackageCount, batch.PackageUnitWeightKg,
            batch.ExpectedMinTempC, batch.ExpectedMaxTempC,
            batch.ExpectedMinHumidityPct, batch.ExpectedMaxHumidityPct,
            batch.ShelfLifeDaysSnapshot, batch.ExpectedDeliveryDate,
            batch.ExpiryDate, batch.Note);
    }

    public async Task<VerifyProductBatchResponse> VerifyAsync(
        long id, int supplierId, int accountId, VerifyProductBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (accountId <= 0) throw new UnauthorizedAccessException();
        var details = ValidateReceiving(request.VerifiedQuantity, request.VerifiedWeightInKg,
            request.VerifiedPackagingType, request.VerifiedPackageCount,
            request.VerifiedPackageUnitWeightKg, request.ReceivingNote);

        var batch = await GetAvailableBatchAsync(id, supplierId, cancellationToken);
        var now = DateTime.UtcNow;
        var history = new BatchStatusHistory
        {
            ProductBatchId = id,
            OldStatus = Submitted,
            NewStatus = PendingQc,
            ChangedByAccountId = accountId,
            ChangeReason = "Product batch physically verified and submitted for QC.",
            ChangedAt = now
        };
        if (!await verificationRepository.ConfirmAsync(id, supplierId,
                details, history, cancellationToken))
            throw new ConflictException(NoLongerAvailable);

        return new VerifyProductBatchResponse(
            id, batch.BatchCode, request.VerifiedQuantity, request.VerifiedWeightInKg, PendingQc);
    }

    public async Task<RejectProductBatchResponse> RejectAsync(
        long id, int supplierId, int accountId, RejectProductBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (accountId <= 0) throw new UnauthorizedAccessException();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new BadRequestException("Rejection reason is required and must not exceed 1000 characters.");

        var batch = await GetAvailableBatchAsync(id, supplierId, cancellationToken);
        var history = new BatchStatusHistory
        {
            ProductBatchId = id,
            OldStatus = Submitted,
            NewStatus = Rejected,
            ChangedByAccountId = accountId,
            ChangeReason = reason,
            ChangedAt = DateTime.UtcNow
        };
        if (!await verificationRepository.RejectAsync(id, supplierId, reason, history, cancellationToken))
            throw new ConflictException(NoLongerAvailable);

        return new RejectProductBatchResponse(id, batch.BatchCode, Rejected, reason);
    }

    public async Task<ProductBatchDetail> UpdateAsync(
        long id, int accountId, UpdateProductBatchReceivingRequest request,
        CancellationToken cancellationToken)
    {
        if (accountId <= 0) throw new UnauthorizedAccessException();
        if (id <= 0) throw new BadRequestException("Invalid batch ID.");
        if (request.ExpectedCreatedAt == default)
            throw new BadRequestException("The batch version is required. Reload the batch and try again.");
        var details = ValidateReceiving(request.VerifiedQuantity, request.VerifiedWeightInKg,
            request.VerifiedPackagingType, request.VerifiedPackageCount,
            request.VerifiedPackageUnitWeightKg, request.ReceivingNote);
        var batch = await repository.GetWarehouseByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Product batch not found.");
        if (await repository.HasQcInspectionAsync(id, cancellationToken))
            throw new ConflictException(QcReceivingLockMessage);
        if (batch.BatchStatus != PendingQc)
            throw new ConflictException("Only batches awaiting QC can be updated.");
        if (batch.CreatedAt != request.ExpectedCreatedAt)
            throw new ConflictException("Batch information changed. Reload the batch and try again.");

        var result = await verificationRepository.UpdateAsync(id, request.ExpectedUpdatedAt,
            request.ExpectedCreatedAt, accountId, details, cancellationToken);
        if (result == ProductBatchReceivingUpdateResult.QcReceived)
            throw new ConflictException(QcReceivingLockMessage);
        if (result != ProductBatchReceivingUpdateResult.Updated)
            throw new ConflictException("Batch information changed. Reload the batch and try again.");

        return await GetAsync(id, cancellationToken);
    }

    private static VerifiedReceivingDetails ValidateReceiving(
        decimal quantity, decimal weight, string? packagingType, int? packageCount,
        decimal? packageUnitWeightKg, string? receivingNote)
    {
        static bool ValidDecimal(decimal value) =>
            value > 0 && value <= 999999999999999.999m && decimal.Round(value, 3) == value;

        if (!ValidDecimal(quantity) || !ValidDecimal(weight))
            throw new BadRequestException("Verified quantity and weight must be positive with at most three decimal places.");
        if (packageCount is <= 0)
            throw new BadRequestException("Verified package count must be greater than zero.");
        if (packageUnitWeightKg.HasValue && !ValidDecimal(packageUnitWeightKg.Value))
            throw new BadRequestException("Verified package unit weight must be positive with at most three decimal places.");

        packagingType = string.IsNullOrWhiteSpace(packagingType) ? null : packagingType.Trim();
        receivingNote = string.IsNullOrWhiteSpace(receivingNote) ? null : receivingNote.Trim();
        if (packagingType?.Length > 100 || receivingNote?.Length > 1000)
            throw new BadRequestException("Verified packaging type or receiving note is too long.");
        return new VerifiedReceivingDetails(quantity, weight, packagingType,
            packageCount, packageUnitWeightKg, receivingNote);
    }

    private async Task<ProductBatch> GetAvailableBatchAsync(
        long id, int supplierId, CancellationToken cancellationToken)
    {
        if (id <= 0 || supplierId <= 0) throw new BadRequestException("Invalid batch or supplier ID.");
        var batch = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Product batch not found.");
        if (batch.SupplierId != supplierId)
            throw new BadRequestException("Declaration does not belong to the selected supplier.");
        if (batch.Supplier is null || batch.CropType is null || batch.GrowingArea is null)
            throw new ConflictException("Product batch declaration is incomplete.");
        if (batch.BatchStatus != Submitted ||
            batch.VerifiedQuantity.HasValue || batch.VerifiedWeightInKg.HasValue)
            throw new ConflictException(NoLongerAvailable);
        return batch;
    }

    private static ProductBatchListItem MapList(ProductBatch batch) => new(
        batch.ProductBatchId, batch.BatchCode, batch.ProductName,
        batch.SupplierId, batch.Supplier.SupplierName,
        batch.CropTypeId, batch.CropType.CropName, batch.CropType.CategoryName,
        batch.DeclaredQuantity, batch.Unit, batch.CreatedAt, batch.UpdatedAt,
        batch.BatchStatus);
}
