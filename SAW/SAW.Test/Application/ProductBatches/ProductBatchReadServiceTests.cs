using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.ProductBatches;

public sealed class ProductBatchReadServiceTests
{
    private readonly Mock<IProductBatchQueryRepository> _query = new();
    private readonly Mock<IProductBatchVerificationRepository> _write = new();

    public ProductBatchReadServiceTests() => _query
        .Setup(q => q.GetRelatedInformationAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(ProductBatchRelatedInformation.Empty);
    private ProductBatchService Service() => new(_query.Object, _write.Object);
    private static ProductBatchQuery Query(int page = 1, int size = 20) => new(null, null, null, null, null, page, size);

    [Fact]
    public async Task Search_normalizes_filters_maps_declaration_and_preserves_pagination()
    {
        var batch = Batch();
        _query.Setup(q => q.SearchAsync(It.IsAny<ProductBatchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ProductBatch>)new[] { batch }, 23));
        var result = await Service().SearchAsync(new("  B-1  ", 2, 3, " pending_qc ", " createdAtAsc ", 2, 10), default);
        Assert.Equal(23, result.TotalCount); Assert.Equal(2, result.Page); Assert.Equal(10, result.PageSize);
        Assert.Equal(10, Assert.Single(result.Items).Quantity); // Declared, not VerifiedQuantity.
        _query.Verify(q => q.SearchAsync(It.Is<ProductBatchQuery>(p => p.BatchCode == "B-1" && p.Status == "PENDING_QC"
            && p.SortBy == "createdAtAsc" && p.SupplierId == 2 && p.CropTypeId == 3), It.IsAny<CancellationToken>()), Times.Once);
        _write.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Search_accepts_page_size_boundaries_and_empty_results(int size)
    {
        _query.Setup(q => q.SearchAsync(It.IsAny<ProductBatchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ProductBatch>)Array.Empty<ProductBatch>(), 0));
        var response = await Service().SearchAsync(Query(size: size) with { BatchCode = "  ", Status = "  " }, default);
        Assert.Empty(response.Items); Assert.Equal(0, response.TotalCount);
        _query.Verify(q => q.SearchAsync(It.Is<ProductBatchQuery>(p => p.BatchCode == null && p.Status == null
            && p.SortBy == "createdAtDesc" && p.PageSize == size), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Search_invalid_pagination_never_queries(int page, int size)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service().SearchAsync(Query(page, size), default));
        _query.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("supplier")]
    [InlineData("crop")]
    [InlineData("longCode")]
    [InlineData("controlCode")]
    [InlineData("status")]
    [InlineData("sort")]
    public async Task Search_invalid_filter_never_queries(string input)
    {
        var query = input switch
        {
            "supplier" => Query() with { SupplierId = 0 },
            "crop" => Query() with { CropTypeId = -1 },
            "longCode" => Query() with { BatchCode = new string('x', 51) },
            "controlCode" => Query() with { BatchCode = "B\n1" },
            "status" => Query() with { Status = "bad status!" },
            _ => Query() with { SortBy = "nameAsc" }
        };
        await Assert.ThrowsAsync<BadRequestException>(() => Service().SearchAsync(query, default));
        _query.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Filter_options_are_delegated_without_writes()
    {
        var options = new ProductBatchFilterOptions([new(2, "Supplier")], [new(3, "Rice")], ["PENDING_QC"]);
        _query.Setup(q => q.GetFilterOptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(options);
        Assert.Same(options, await Service().GetFilterOptionsAsync(default));
        _write.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Detail_keeps_declaration_receiving_notes_and_unspecified_values_separate()
    {
        var batch = Batch();
        _query.Setup(q => q.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        var detail = await Service().GetAsync(7, default);
        Assert.Equal(10, detail.Quantity); Assert.Equal(100, detail.WeightInKg);
        Assert.Equal(12, detail.VerifiedQuantity); Assert.Equal(125, detail.VerifiedWeightInKg);
        Assert.Equal("Supplier note", detail.Note); Assert.Equal("Staff note", detail.ReceivingNote);
        Assert.Equal("Supplier box", detail.PackagingType); Assert.Null(detail.VerifiedPackagingType);
        Assert.Equal(10, detail.PackageCount); Assert.Null(detail.VerifiedPackageCount);
        Assert.Equal(10, detail.PackageUnitWeightKg); Assert.Null(detail.VerifiedPackageUnitWeightKg);
        Assert.Equal("Damaged", detail.RejectionReason); Assert.Equal(5, detail.ExpectedMinTempC);
        Assert.Equal(80, detail.ExpectedMaxHumidityPct); Assert.Equal(30, detail.ShelfLifeDaysSnapshot);
        _write.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Detail_without_related_records_returns_empty_sections_without_inventing_history_or_qc()
    {
        _query.Setup(q => q.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Batch());
        var detail = await Service().GetAsync(7, default);
        Assert.Null(detail.LatestQcInspection);
        Assert.Empty(detail.GoodsReceipts); Assert.Empty(detail.StatusHistory); Assert.Empty(detail.WarehouseTransactions);
        _write.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("COMPLETED", "PASS", "E")]
    [InlineData("COMPLETED", "FAIL", "B")]
    [InlineData("DRAFT", null, null)]
    [InlineData("IN_PROGRESS", null, null)]
    public async Task Detail_maps_saved_related_records_without_reinterpreting_qc(string status, string? result, string? grade)
    {
        var now = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        var related = new ProductBatchRelatedInformation(new(3, "QC-LATEST", status, result, grade, now,
                status == "COMPLETED" ? now.AddMinutes(1) : null, "QC Staff", null),
            [new(4, "GR-DRAFT", "DRAFT", now, null, 1, "A1", "Receiving staff", "Receipt note", 12, "Box", 125)],
            [new(5, now, "SUBMITTED", "PENDING_QC", null, null)],
            [new(6, "STOCK_TRANSFER", "ST-1", "COMPLETED", now, 5, "kg", null, "A1", "B1", "Staff", "Move")]);
        _query.Setup(q => q.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Batch());
        _query.Setup(q => q.GetRelatedInformationAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(related);
        _query.Setup(q => q.HasQcInspectionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var detail = await Service().GetAsync(7, default);
        Assert.Same(related.LatestQcInspection, detail.LatestQcInspection);
        Assert.Equal(result, detail.LatestQcInspection!.QcResult); Assert.Equal(grade, detail.LatestQcInspection.QualityGrade);
        Assert.Same(related.GoodsReceipts, detail.GoodsReceipts); Assert.Same(related.StatusHistory, detail.StatusHistory);
        Assert.Same(related.WarehouseTransactions, detail.WarehouseTransactions);
        Assert.False(detail.CanUpdateReceivingInformation); _write.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Detail_missing_batch_returns_not_found_without_writes(long id)
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(id, default));
        _write.VerifyNoOtherCalls();
    }

    private static ProductBatch Batch() => new()
    {
        ProductBatchId = 7, BatchCode = "B-1", ProductName = "Rice", SupplierId = 2,
        Supplier = new() { SupplierName = "Supplier" }, CropTypeId = 3,
        CropType = new() { CropName = "Rice", CategoryName = "Grain" }, GrowingArea = new() { AreaName = "Farm" },
        DeclaredQuantity = 10, WeightInKg = 100, Unit = "Box", VerifiedQuantity = 12, VerifiedWeightInKg = 125,
        Note = "Supplier note", ReceivingNote = "Staff note", PackagingType = "Supplier box", PackageCount = 10,
        PackageUnitWeightKg = 10, RejectionReason = "Damaged", ExpectedMinTempC = 5,
        ExpectedMaxHumidityPct = 80, ShelfLifeDaysSnapshot = 30, BatchStatus = "PENDING_QC"
    };
}
