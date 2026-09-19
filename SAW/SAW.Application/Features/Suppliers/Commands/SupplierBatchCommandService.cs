using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Application.Features.Suppliers.Commands;

public class SupplierBatchCommandService : ISupplierBatchCommandService
{
    private readonly IProductBatchRepository _productBatchRepository;
    private readonly ISupplierRepository _supplierRepository;

    public SupplierBatchCommandService(
        IProductBatchRepository productBatchRepository,
        ISupplierRepository supplierRepository)
    {
        _productBatchRepository = productBatchRepository;
        _supplierRepository = supplierRepository;
    }

    public async Task<SupplierBatchListResponse> GetDeclaredBatchesAsync(int currentAccountId, GetSupplierBatchesQueryRequest request, CancellationToken cancellationToken = default)
    {
        return await _productBatchRepository.GetBatchesBySupplierAccountIdAsync(currentAccountId, request, cancellationToken);
    }

    public async Task<SupplierBatchItemResponse> DeclareBatchAsync(int currentAccountId, DeclareProductBatchRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Lấy thông tin Supplier Profile từ AccountId
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Please declare supplier information before declaring product batch information.");
        }

        // 2. Validate HarvestDate (không được ở tương lai)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.HarvestDate > today)
        {
            throw new ArgumentException("Harvest date cannot be in the future.");
        }

        // 3. Validate ExpectedDeliveryDate
        if (request.ExpectedDeliveryDate.HasValue && request.ExpectedDeliveryDate.Value < request.HarvestDate)
        {
            throw new ArgumentException("Invalid expected delivery date.");
        }

        // 4. Kiểm tra CropType có được đăng ký bởi Supplier này không
        var isRegistered = await _productBatchRepository.IsCropTypeRegisteredForSupplierAsync(supplier.SupplierId, request.CropTypeId, cancellationToken);
        if (!isRegistered)
        {
            throw new InvalidOperationException("The selected crop type is not registered for this supplier.");
        }

        // 5. Tính toán WeightInKg theo Đơn vị & Quy cách đóng gói
        decimal calculatedWeightKg = 0;
        var unitLower = request.Unit.Trim().ToLower();

        if (unitLower == "kg" || unitLower == "kilogram")
        {
            calculatedWeightKg = request.DeclaredQuantity;
        }
        else if (unitLower == "tấn" || unitLower == "tan" || unitLower == "ton")
        {
            calculatedWeightKg = request.DeclaredQuantity * 1000m;
        }
        else if (request.PackageCount.HasValue && request.PackageUnitWeightKg.HasValue)
        {
            calculatedWeightKg = request.PackageCount.Value * request.PackageUnitWeightKg.Value;
        }
        else
        {
            calculatedWeightKg = request.DeclaredQuantity; // Fallback
        }

        if (calculatedWeightKg <= 0)
        {
            throw new ArgumentException("Declared quantity must be greater than 0.");
        }

        // 6. Sinh mã lô hàng tự động
        var batchCode = await _productBatchRepository.GenerateBatchCodeAsync(cancellationToken);

        // 7. Tạo Entity ProductBatch
        var newBatch = new ProductBatch
        {
            BatchCode = batchCode,
            SupplierId = supplier.SupplierId,
            CropTypeId = request.CropTypeId,
            ProductName = request.ProductName.Trim(),
            Origin = request.Origin.Trim(),
            HarvestDate = request.HarvestDate,
            DeclaredQuantity = request.DeclaredQuantity,
            Unit = request.Unit.Trim(),
            WeightInKg = calculatedWeightKg,
            PackagingType = request.PackagingType,
            PackageCount = request.PackageCount,
            PackageUnitWeightKg = request.PackageUnitWeightKg,
            ExpectedMinTempC = request.ExpectedMinTempC,
            ExpectedMaxTempC = request.ExpectedMaxTempC,
            ExpectedMinHumidityPct = request.ExpectedMinHumidityPct,
            ExpectedMaxHumidityPct = request.ExpectedMaxHumidityPct,
            ExpectedDeliveryDate = request.ExpectedDeliveryDate,
            ExpiryDate = request.ExpiryDate,
            BatchStatus = "SUBMITTED", // Gán trạng thái ban đầu SUBMITTED
            Note = request.Note,
            CreatedAt = DateTime.UtcNow
        };

        // 8. Tạo lịch sử trạng thái ban đầu (BatchStatusHistory)
        var statusHistory = new BatchStatusHistory
        {
            OldStatus = null,
            NewStatus = "SUBMITTED",
            ChangedByAccountId = currentAccountId,
            ChangeReason = "Khai báo lô hàng mới từ phía Nhà cung cấp.",
            ChangedAt = DateTime.UtcNow
        };

        // 9. Lưu vào DB
        await _productBatchRepository.AddProductBatchAsync(newBatch, statusHistory, cancellationToken);

        // 10. Trả về Response
        return new SupplierBatchItemResponse
        {
            BatchId = newBatch.ProductBatchId,
            BatchCode = newBatch.BatchCode,
            ProductName = newBatch.ProductName,
            QuantityInTons = newBatch.Unit.ToLower().Contains("kg") ? newBatch.DeclaredQuantity / 1000m : newBatch.DeclaredQuantity,
            SubmittedDate = newBatch.CreatedAt,
            Status = newBatch.BatchStatus,
            StatusDisplayName = "Chờ duyệt"
        };
    }

    public async Task<SupplierBatchItemResponse> UpdateDeclaredBatchAsync(long batchId, int currentAccountId, UpdateProductBatchRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra Nhà cung cấp hiện tại
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new UnauthorizedAccessException("You are not allowed to edit this batch.");
        }

        // 2. Tìm lô hàng theo BatchId
        var existingBatch = await _productBatchRepository.GetBatchByIdAsync(batchId, cancellationToken);
        if (existingBatch == null)
        {
            throw new KeyNotFoundException("Product batch not found.");
        }

        // 3. Kiểm tra lô hàng có thuộc sở hữu của Nhà cung cấp này không
        if (existingBatch.SupplierId != supplier.SupplierId)
        {
            throw new UnauthorizedAccessException("You are not allowed to edit this batch.");
        }

        // 4. Kiểm tra trạng thái lô hàng (chỉ cho phép sửa khi SUBMITTED)
        if (existingBatch.BatchStatus != "SUBMITTED")
        {
            throw new InvalidOperationException("This batch declaration can no longer be modified.");
        }

        // 5. Kiểm tra HarvestDate không ở tương lai
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.HarvestDate > today)
        {
            throw new ArgumentException("Harvest date cannot be in the future.");
        }

        // 6. Kiểm tra ExpectedDeliveryDate
        if (request.ExpectedDeliveryDate.HasValue && request.ExpectedDeliveryDate.Value < request.HarvestDate)
        {
            throw new ArgumentException("Invalid expected delivery date.");
        }

        // 7. Kiểm tra CropType có được đăng ký bởi Supplier này không
        var isRegistered = await _productBatchRepository.IsCropTypeRegisteredForSupplierAsync(supplier.SupplierId, request.CropTypeId, cancellationToken);
        if (!isRegistered)
        {
            throw new InvalidOperationException("The selected crop type is not registered for this supplier.");
        }

        // 8. Tính toán lại WeightInKg
        decimal calculatedWeightKg = 0;
        var unitLower = request.Unit.Trim().ToLower();

        if (unitLower == "kg" || unitLower == "kilogram")
        {
            calculatedWeightKg = request.DeclaredQuantity;
        }
        else if (unitLower == "tấn" || unitLower == "tan" || unitLower == "ton")
        {
            calculatedWeightKg = request.DeclaredQuantity * 1000m;
        }
        else if (request.PackageCount.HasValue && request.PackageUnitWeightKg.HasValue)
        {
            calculatedWeightKg = request.PackageCount.Value * request.PackageUnitWeightKg.Value;
        }
        else
        {
            calculatedWeightKg = request.DeclaredQuantity;
        }

        if (calculatedWeightKg <= 0)
        {
            throw new ArgumentException("Declared quantity must be greater than 0.");
        }

        // 9. Cập nhật thông tin Lô hàng
        existingBatch.CropTypeId = request.CropTypeId;
        existingBatch.ProductName = request.ProductName.Trim();
        existingBatch.Origin = request.Origin.Trim();
        existingBatch.HarvestDate = request.HarvestDate;
        existingBatch.DeclaredQuantity = request.DeclaredQuantity;
        existingBatch.Unit = request.Unit.Trim();
        existingBatch.WeightInKg = calculatedWeightKg;
        existingBatch.PackagingType = request.PackagingType;
        existingBatch.PackageCount = request.PackageCount;
        existingBatch.PackageUnitWeightKg = request.PackageUnitWeightKg;
        existingBatch.ExpectedMinTempC = request.ExpectedMinTempC;
        existingBatch.ExpectedMaxTempC = request.ExpectedMaxTempC;
        existingBatch.ExpectedMinHumidityPct = request.ExpectedMinHumidityPct;
        existingBatch.ExpectedMaxHumidityPct = request.ExpectedMaxHumidityPct;
        existingBatch.ExpectedDeliveryDate = request.ExpectedDeliveryDate;
        existingBatch.ExpiryDate = request.ExpiryDate;
        existingBatch.Note = request.Note;

        // 10. Ghi nhận lịch sử thay đổi (Audit History)
        var statusHistory = new BatchStatusHistory
        {
            ProductBatchId = existingBatch.ProductBatchId,
            OldStatus = existingBatch.BatchStatus,
            NewStatus = existingBatch.BatchStatus, // Vẫn là SUBMITTED
            ChangedByAccountId = currentAccountId,
            ChangeReason = "Cập nhật thông tin khai báo lô hàng.",
            ChangedAt = DateTime.UtcNow
        };

        // 11. Lưu xuống DB
        await _productBatchRepository.UpdateProductBatchAsync(existingBatch, statusHistory, cancellationToken);

        // 12. Trả về Response
        return new SupplierBatchItemResponse
        {
            BatchId = existingBatch.ProductBatchId,
            BatchCode = existingBatch.BatchCode,
            ProductName = existingBatch.ProductName,
            QuantityInTons = existingBatch.Unit.ToLower().Contains("kg") ? existingBatch.DeclaredQuantity / 1000m : existingBatch.DeclaredQuantity,
            SubmittedDate = existingBatch.CreatedAt,
            Status = existingBatch.BatchStatus,
            StatusDisplayName = "Chờ duyệt"
        };
    }

    public async Task<SupplierBatchStatusResponse> GetBatchStatusDetailAsync(long batchId, int currentAccountId, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra Hồ sơ Nhà cung cấp
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new UnauthorizedAccessException("Supplier profile not found.");
        }

        // 2. Lấy thông tin lô hàng chi tiết
        var batch = await _productBatchRepository.GetBatchStatusDetailByIdAsync(batchId, cancellationToken);
        if (batch == null)
        {
            throw new KeyNotFoundException("Product batch not found.");
        }

        // 3. Kiểm tra quyền truy cập (Chỉ cho phép xem lô hàng của chính mình)
        if (batch.SupplierId != supplier.SupplierId)
        {
            throw new UnauthorizedAccessException("You are not allowed to view this batch.");
        }

        // 4. Tính tổng ReceivedQuantity từ các GoodsReceipt có trạng thái COMMITTED
        decimal receivedQuantity = await _productBatchRepository.GetCommittedReceivedQuantityAsync(batchId, cancellationToken);

        // 5. Lấy kết quả QC & Quality Grade gần nhất (nếu có)
        var latestQc = batch.QcInspections?
            .OrderByDescending(q => q.CompletedAt ?? q.StartedAt)
            .FirstOrDefault();

        string? qcResult = latestQc?.QcResult;
        string? qualityGrade = latestQc?.QualityGrade;
        string? rejectionReason = (latestQc?.QcResult == "FAILED" || latestQc?.QcResult == "REJECTED")
            ? latestQc.Note
            : null;

        // 6. Lấy Lịch sử trạng thái xử lý
        var histories = await _productBatchRepository.GetBatchStatusHistoryAsync(batchId, cancellationToken);
        var historyDtos = histories.Select(h => new BatchStatusHistoryDto
        {
            OldStatus = h.OldStatus,
            NewStatus = h.NewStatus,
            ChangeReason = h.ChangeReason,
            ChangedAt = h.ChangedAt
        }).ToList();

        // 7. Map dữ liệu trả về Response
        return new SupplierBatchStatusResponse
        {
            BatchId = batch.ProductBatchId,
            BatchCode = batch.BatchCode,
            ProductName = batch.ProductName,
            CropTypeName = batch.CropType?.CropName ?? string.Empty,
            Origin = batch.Origin,
            HarvestDate = batch.HarvestDate,
            DeclaredQuantity = batch.DeclaredQuantity,
            Unit = batch.Unit,
            ReceivedQuantity = receivedQuantity,
            WeightInKg = batch.WeightInKg,
            CurrentStatus = batch.BatchStatus,
            StatusDisplayName = GetStatusDisplayName(batch.BatchStatus),
            QcResult = qcResult,
            QualityGrade = qualityGrade,
            RejectionReason = rejectionReason,
            WarehouseNote = batch.Note,
            ExpectedDeliveryDate = batch.ExpectedDeliveryDate,
            CreatedAt = batch.CreatedAt,
            StatusHistory = historyDtos
        };
    }

    public async Task CancelBatchAsync(long batchId, int currentAccountId, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra Hồ sơ Nhà cung cấp từ currentAccountId
        var supplier = await _supplierRepository.GetEntityByAccountIdAsync(currentAccountId, cancellationToken);
        if (supplier == null)
        {
            throw new UnauthorizedAccessException("You are not allowed to cancel this batch.");
        }

        // 2. Tìm lô hàng theo BatchId
        var existingBatch = await _productBatchRepository.GetBatchByIdAsync(batchId, cancellationToken);
        if (existingBatch == null)
        {
            throw new KeyNotFoundException("Product batch not found.");
        }

        // 3. Kiểm tra lô hàng có thuộc sở hữu của Nhà cung cấp này không
        if (existingBatch.SupplierId != supplier.SupplierId)
        {
            throw new UnauthorizedAccessException("You are not allowed to cancel this batch.");
        }

        // 4. Kiểm tra trạng thái lô hàng (chỉ cho phép hủy khi đang SUBMITTED)
        if (existingBatch.BatchStatus != "SUBMITTED")
        {
            throw new InvalidOperationException("This batch cannot be cancelled at its current status.");
        }

        // 5. Cập nhật trạng thái lô hàng thành CANCELLED
        var oldStatus = existingBatch.BatchStatus;
        existingBatch.BatchStatus = "CANCELLED";

        // 6. Ghi nhận lịch sử thay đổi trạng thái
        var statusHistory = new BatchStatusHistory
        {
            ProductBatchId = existingBatch.ProductBatchId,
            OldStatus = oldStatus,
            NewStatus = "CANCELLED",
            ChangedByAccountId = currentAccountId,
            ChangeReason = "Hủy khai báo lô hàng từ phía Nhà cung cấp.",
            ChangedAt = DateTime.UtcNow
        };

        // 7. Lưu xuống DB qua Repository
        await _productBatchRepository.UpdateProductBatchAsync(existingBatch, statusHistory, cancellationToken);
    }

    private static string GetStatusDisplayName(string status) => status switch
    {
        "SUBMITTED" => "Đã khai báo / Chờ duyệt",
        "APPROVED" => "Đã duyệt",
        "REJECTED" => "Đã từ chối",
        "RECEIVING" => "Đang nhận hàng",
        "RECEIVED" => "Đã nhận hàng",
        "IN_QC" => "Đang kiểm định QC",
        "QC_PASSED" => "QC Đạt",
        "QC_FAILED" => "QC Không đạt",
        "STORED" => "Đã nhập kho",
        "CANCELLED" => "Đã hủy",
        _ => status
    };
}