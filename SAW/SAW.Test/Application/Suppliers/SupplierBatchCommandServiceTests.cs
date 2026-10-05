using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Test.Application.Suppliers;

public class SupplierBatchCommandServiceTests
{
    private readonly Mock<IProductBatchRepository> _batches = new();
    private readonly Mock<ISupplierRepository> _suppliers = new();
    private SupplierBatchCommandService Service => new(_batches.Object, _suppliers.Object);
    private readonly DateTime _created = new(2026, 1, 1);

    public SupplierBatchCommandServiceTests()
    {
        _suppliers.Setup(r => r.GetEntityByAccountIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Supplier { SupplierId = 2, AccountId = 1, ProfileStatus = "ACTIVE" });
        _batches.Setup(r => r.IsCropTypeRegisteredForSupplierAsync(2, 3, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _batches.Setup(r => r.IsGrowingAreaRegisteredForSupplierAsync(2, 4, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _batches.Setup(r => r.GetBatchByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Batch());
    }
    private ProductBatch Batch(string status = "SUBMITTED", int supplierId = 2) => new()
    {
        ProductBatchId = 5, BatchCode = "LH-TEST", SupplierId = supplierId, BatchStatus = status,
        CropTypeId = 3, GrowingAreaId = 4, CreatedAt = _created
    };
    private UpdateProductBatchRequest Request() => new()
    {
        CropTypeId = 3, GrowingAreaId = 4, ProductName = "Cam", HarvestDate = new(2026, 1, 1),
        Unit = "Kg", DeclaredQuantity = 250, ExpectedCreatedAt = _created
    };

    [Theory]
    [InlineData("PENDING_QC")]
    [InlineData("APPROVED_FOR_STORAGE")]
    [InlineData("REJECTED")]
    [InlineData("IN_STOCK")]
    [InlineData("CANCELLED")]
    public async Task UpdateAndCancelRejectProcessedBatches(string status)
    {
        _batches.Setup(r => r.GetBatchByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Batch(status));
        await Assert.ThrowsAsync<ConflictException>(() => Service.UpdateDeclaredBatchAsync(5, 1, Request()));
        await Assert.ThrowsAsync<ConflictException>(() => Service.CancelBatchAsync(5, 1, new() { ExpectedCreatedAt = _created }));
    }

    [Fact]
    public async Task CannotUpdateAnotherSuppliersBatch()
    {
        _batches.Setup(r => r.GetBatchByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Batch(supplierId: 99));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.UpdateDeclaredBatchAsync(5, 1, Request()));
    }

    [Fact]
    public async Task CannotUseAnotherSuppliersGrowingArea()
    {
        _batches.Setup(r => r.IsGrowingAreaRegisteredForSupplierAsync(2, 4, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.UpdateDeclaredBatchAsync(5, 1, Request()));
    }

    [Fact]
    public async Task UpdateCarriesLoadedVersionAndDocumentsToAtomicWrite()
    {
        var request = Request(); request.ExpectedUpdatedAt = _created.AddSeconds(10); request.EvidenceDocumentUrls = ["file-reference"];
        var result = await Service.UpdateDeclaredBatchAsync(5, 1, request);
        Assert.Equal(0.25m, result.QuantityInTons);
        _batches.Verify(r => r.UpdateProductBatchAsync(It.Is<ProductBatch>(b => b.WeightInKg == 250m),
            It.Is<BatchStatusHistory>(h => h.OldStatus == h.NewStatus && h.ChangedByAccountId == 1),
            _created, request.ExpectedUpdatedAt, request.EvidenceDocumentUrls, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentChangeRemainsAConflict()
    {
        _batches.Setup(r => r.UpdateProductBatchAsync(It.IsAny<ProductBatch>(), It.IsAny<BatchStatusHistory>(),
            It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("Reload"));
        await Assert.ThrowsAsync<ConflictException>(() => Service.UpdateDeclaredBatchAsync(5, 1, Request()));
    }

    [Fact]
    public async Task DetailKeepsIdentifiersAndDeclarationSeparateFromReceiving()
    {
        var batch = Batch(); batch.Note = "Supplier note"; batch.ReceivingNote = "Receiving note";
        batch.VerifiedQuantity = 240; batch.VerifiedWeightInKg = 239;
        _batches.Setup(r => r.GetBatchStatusDetailByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _batches.Setup(r => r.GetBatchStatusHistoryAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _batches.Setup(r => r.GetDocumentsAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var result = await Service.GetBatchStatusDetailAsync(5, 1);
        Assert.Equal(4, result.GrowingAreaId); Assert.Equal(3, result.CropTypeId);
        Assert.Equal("Supplier note", result.SupplierNote); Assert.Equal("Receiving note", result.WarehouseNote);
        Assert.Equal(240m, result.VerifiedQuantity); Assert.Equal(_created, result.CreatedAt);
    }

    private void SetupDetail(ProductBatch batch)
    {
        _batches.Setup(r => r.GetBatchStatusDetailByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _batches.Setup(r => r.GetBatchStatusHistoryAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _batches.Setup(r => r.GetDocumentsAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("IN_PROGRESS")]
    [InlineData("COMPLETED")]
    public async Task DetailExposesLatestInspectionStatusWithoutChangingBatchStatus(string inspectionStatus)
    {
        var batch = Batch("PENDING_QC");
        batch.QcInspections.Add(new QcInspection { QcInspectionId = 1, StartedAt = _created,
            InspectionStatus = "COMPLETED", CompletedAt = _created.AddHours(1), QcResult = "PASS" });
        batch.QcInspections.Add(new QcInspection { QcInspectionId = 2, StartedAt = _created.AddDays(1),
            InspectionStatus = inspectionStatus, CompletedAt = inspectionStatus == "COMPLETED" ? _created.AddDays(2) : null,
            QcResult = inspectionStatus == "COMPLETED" ? "FAIL" : null, Note = "Inspection reason" });
        SetupDetail(batch);
        var result = await Service.GetBatchStatusDetailAsync(5, 1);
        Assert.Equal(inspectionStatus, result.QcInspectionStatus);
        Assert.Equal("PENDING_QC", result.CurrentStatus);
        Assert.Equal(inspectionStatus == "COMPLETED" ? "FAIL" : "PASS", result.QcResult);
        Assert.Equal(inspectionStatus == "COMPLETED" ? _created.AddDays(2) : _created.AddHours(1), result.QcCompletedAt);
        Assert.Equal(inspectionStatus == "COMPLETED" ? "Inspection reason" : null, result.RejectionReason);
        _batches.Verify(r => r.UpdateProductBatchAsync(It.IsAny<ProductBatch>(), It.IsAny<BatchStatusHistory>(),
            It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetailUsesCommittedWarehouseDateAndIgnoresLaterInspections()
    {
        var batch = Batch("ISSUED");
        batch.GoodsReceipts.Add(new GoodsReceipt { ReceiptStatus = "DRAFT", CommittedAt = _created });
        batch.GoodsReceipts.Add(new GoodsReceipt { ReceiptStatus = "COMMITTED", CommittedAt = _created.AddDays(2) });
        batch.GoodsReceipts.Add(new GoodsReceipt { ReceiptStatus = "COMMITTED", CommittedAt = _created.AddDays(3) });
        batch.QcInspections.Add(new QcInspection { QcInspectionId = 1, StartedAt = _created,
            InspectionStatus = "COMPLETED", CompletedAt = _created.AddDays(1), QcResult = "PASS" });
        batch.QcInspections.Add(new QcInspection { QcInspectionId = 2, StartedAt = _created.AddDays(4),
            InspectionStatus = "IN_PROGRESS" });
        SetupDetail(batch);
        var result = await Service.GetBatchStatusDetailAsync(5, 1);
        Assert.Equal(_created.AddDays(2), result.WarehousedAt);
        Assert.Equal("COMPLETED", result.QcInspectionStatus);
        Assert.Equal("PASS", result.QcResult);
        Assert.Equal(_created.AddDays(1), result.QcCompletedAt);
        Assert.Equal("ISSUED", result.CurrentStatus);
    }

    [Fact]
    public async Task DetailRejectsOtherSuppliersBatchBeforeReadingHistory()
    {
        SetupDetail(Batch(supplierId: 99));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.GetBatchStatusDetailAsync(5, 1));
        _batches.Verify(r => r.GetBatchStatusHistoryAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
