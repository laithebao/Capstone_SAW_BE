using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.ProductBatches;

public sealed class ProductBatchVerificationTests
{
    private readonly Mock<IProductBatchQueryRepository> _query = new();
    private readonly Mock<IProductBatchVerificationRepository> _verification = new();

    private ProductBatchService Service() => new(_query.Object, _verification.Object);

    private static ProductBatch SubmittedBatch() => new()
    {
        ProductBatchId = 7,
        BatchCode = "BATCH001",
        SupplierId = 2,
        Supplier = new Supplier { SupplierId = 2, SupplierName = "Supplier" },
        CropTypeId = 3,
        CropType = new CropType { CropTypeId = 3, CropName = "Rice" },
        GrowingAreaId = 4,
        GrowingArea = new GrowingArea { GrowingAreaId = 4, AreaName = "Area" },
        ProductName = "Rice",
        DeclaredQuantity = 1000m,
        WeightInKg = 1000m,
        Unit = "kg",
        CreatedAt = new DateTime(2026, 9, 28, 9, 0, 0),
        BatchStatus = "SUBMITTED"
    };

    private void SetupBatch(ProductBatch? batch)
    {
        _query.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _verification.Setup(x => x.ConfirmAsync(7, 2, It.IsAny<VerifiedReceivingDetails>(),
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task SubmittedBatch_IsVerifiedWithoutChangingDeclarationOrCreatingAnotherBatch()
    {
        var batch = SubmittedBatch();
        SetupBatch(batch);

        var result = await Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m, "Box", 10, 94m, "Received"), CancellationToken.None);

        Assert.Equal(7, result.Id);
        Assert.Equal("BATCH001", result.BatchCode);
        Assert.Equal(940m, result.VerifiedQuantity);
        Assert.Equal(940m, result.VerifiedWeightInKg);
        Assert.Equal("PENDING_QC", result.BatchStatus);
        Assert.Equal(1000m, batch.DeclaredQuantity);
        Assert.Equal(1000m, batch.WeightInKg);
        Assert.Null(batch.VerifiedQuantity);
        Assert.Null(batch.VerifiedWeightInKg);
        _verification.Verify(x => x.ConfirmAsync(7, 2,
            It.Is<VerifiedReceivingDetails>(details => details.VerifiedQuantity == 940m &&
                details.VerifiedWeightInKg == 940m && details.VerifiedPackagingType == "Box" &&
                details.VerifiedPackageCount == 10 && details.VerifiedPackageUnitWeightKg == 94m &&
                details.ReceivingNote == "Received"),
            It.Is<BatchStatusHistory>(history =>
                history.ProductBatchId == 7 && history.OldStatus == "SUBMITTED" &&
                history.NewStatus == "PENDING_QC" && history.ChangedByAccountId == 11),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 940)]
    [InlineData(-1, 940)]
    [InlineData(940, 0)]
    [InlineData(940, -1)]
    public async Task NonPositiveVerifiedValues_AreRejected(decimal quantity, decimal weight)
    {
        SetupBatch(SubmittedBatch());
        await Assert.ThrowsAsync<BadRequestException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(quantity, weight), CancellationToken.None));
        _verification.Verify(x => x.ConfirmAsync(It.IsAny<long>(), It.IsAny<int>(),
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<BatchStatusHistory>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BatchNotSubmitted_IsRejected()
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "PENDING_QC";
        SetupBatch(batch);
        await Assert.ThrowsAsync<ConflictException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None));
    }

    [Fact]
    public async Task MissingBatch_IsRejected()
    {
        SetupBatch(null);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None));
    }

    [Fact]
    public async Task BatchBelongingToAnotherSupplier_IsRejected()
    {
        SetupBatch(SubmittedBatch());
        await Assert.ThrowsAsync<BadRequestException>(() => Service().VerifyAsync(7, 3, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None));
    }

    [Fact]
    public async Task QuantityAboveDeclared_IsAccepted()
    {
        SetupBatch(SubmittedBatch());
        var result = await Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(1040m, 1040m), CancellationToken.None);
        Assert.Equal(1040m, result.VerifiedQuantity);
        _verification.Verify(x => x.ConfirmAsync(7, 2,
            It.Is<VerifiedReceivingDetails>(details => details.VerifiedQuantity == 1040m &&
                details.VerifiedWeightInKg == 1040m),
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentVerification_IsRejected()
    {
        SetupBatch(SubmittedBatch());
        _verification.Setup(x => x.ConfirmAsync(7, 2,
            It.IsAny<VerifiedReceivingDetails>(),
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ConflictException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_TrimsOptionalTextAndPreservesNullablePackaging()
    {
        SetupBatch(SubmittedBatch());
        await Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(1100m, 900m, "  Crate  ", null, null, "  Good condition  "),
            CancellationToken.None);
        _verification.Verify(x => x.ConfirmAsync(7, 2,
            It.Is<VerifiedReceivingDetails>(details =>
                details.VerifiedQuantity == 1100m && details.VerifiedWeightInKg == 900m &&
                details.VerifiedPackagingType == "Crate" && details.VerifiedPackageCount == null &&
                details.VerifiedPackageUnitWeightKg == null && details.ReceivingNote == "Good condition"),
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidPackageCount_IsRejected(int count)
    {
        SetupBatch(SubmittedBatch());
        await Assert.ThrowsAsync<BadRequestException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(100m, 100m, VerifiedPackageCount: count), CancellationToken.None));
    }

    [Fact]
    public async Task Reject_OnlyNeedsAReason_AndDoesNotConfirmReceivingDetails()
    {
        SetupBatch(SubmittedBatch());
        _verification.Setup(x => x.RejectAsync(7, 2, "Damaged", It.IsAny<BatchStatusHistory>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Service().RejectAsync(7, 2, 11,
            new RejectProductBatchRequest("  Damaged  "), CancellationToken.None);

        Assert.Equal("REJECTED", result.BatchStatus);
        Assert.Equal("Damaged", result.RejectionReason);
        _verification.Verify(x => x.RejectAsync(7, 2, "Damaged",
            It.Is<BatchStatusHistory>(h => h.OldStatus == "SUBMITTED" &&
                h.NewStatus == "REJECTED" && h.ChangedByAccountId == 11),
            It.IsAny<CancellationToken>()), Times.Once);
        _verification.Verify(x => x.ConfirmAsync(It.IsAny<long>(), It.IsAny<int>(),
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<BatchStatusHistory>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Reject_RequiresTrimmedReason(string? reason)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service().RejectAsync(7, 2, 11,
            new RejectProductBatchRequest(reason), CancellationToken.None));
    }

    [Fact]
    public async Task Reject_RejectsOverlongReason()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service().RejectAsync(7, 2, 11,
            new RejectProductBatchRequest(new string('x', 1001)), CancellationToken.None));
    }

    [Fact]
    public async Task ReceivingValues_RejectExcessPrecision()
    {
        SetupBatch(SubmittedBatch());
        await Assert.ThrowsAsync<BadRequestException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(100.0001m, 100m), CancellationToken.None));
        await Assert.ThrowsAsync<BadRequestException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(100m, 100m, VerifiedPackageUnitWeightKg: 0m),
            CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentReject_ReturnsConflict()
    {
        SetupBatch(SubmittedBatch());
        _verification.Setup(x => x.RejectAsync(7, 2, "Damaged", It.IsAny<BatchStatusHistory>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ConflictException>(() => Service().RejectAsync(7, 2, 11,
            new RejectProductBatchRequest("Damaged"), CancellationToken.None));
    }

    [Fact]
    public async Task Update_UsesTheStoredVersion_AndCanClearOptionalValues()
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "PENDING_QC";
        batch.UpdatedAt = new DateTime(2026, 9, 28, 10, 0, 0);
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _verification.Setup(x => x.UpdateAsync(7, batch.UpdatedAt, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>())).ReturnsAsync(ProductBatchReceivingUpdateResult.Updated);

        await Service().UpdateAsync(7, 11, new UpdateProductBatchReceivingRequest(
            1200m, 1100m, batch.UpdatedAt, batch.CreatedAt, "   ", null, null, "  "), CancellationToken.None);

        _verification.Verify(x => x.UpdateAsync(7, batch.UpdatedAt, batch.CreatedAt, 11,
            It.Is<VerifiedReceivingDetails>(details =>
                details.VerifiedQuantity == 1200m && details.VerifiedWeightInKg == 1100m &&
                details.VerifiedPackagingType == null && details.VerifiedPackageCount == null &&
                details.VerifiedPackageUnitWeightKg == null && details.ReceivingNote == null),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("PENDING_QC", batch.BatchStatus);
        Assert.Equal(1000m, batch.DeclaredQuantity);
    }

    [Fact]
    public async Task ConcurrentUpdate_ReturnsConflict()
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "PENDING_QC";
        batch.UpdatedAt = new DateTime(2026, 9, 28, 10, 0, 0);
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _verification.Setup(x => x.UpdateAsync(7, batch.UpdatedAt, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>())).ReturnsAsync(ProductBatchReceivingUpdateResult.Conflict);
        await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, batch.UpdatedAt, batch.CreatedAt), CancellationToken.None));
    }

    [Fact]
    public async Task Update_RequiresPendingQc()
    {
        var batch = SubmittedBatch();
        batch.UpdatedAt = new DateTime(2026, 9, 28, 10, 0, 0);
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, batch.UpdatedAt, batch.CreatedAt), CancellationToken.None));
        _verification.Verify(x => x.UpdateAsync(It.IsAny<long>(), It.IsAny<DateTime?>(),
            It.IsAny<DateTime>(),
            It.IsAny<int>(), It.IsAny<VerifiedReceivingDetails>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_RequiresVersionAndAuthenticatedActor()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().UpdateAsync(7, 0,
            new UpdateProductBatchReceivingRequest(100m, 100m, DateTime.UtcNow, SubmittedBatch().CreatedAt), CancellationToken.None));
        await Assert.ThrowsAsync<BadRequestException>(() => Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, null, default), CancellationToken.None));
    }

    [Fact]
    public async Task Update_MissingBatch_ReturnsNotFound()
    {
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProductBatch?)null);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, DateTime.UtcNow, SubmittedBatch().CreatedAt), CancellationToken.None));
    }

    [Fact]
    public async Task ExistingPendingQcBatchWithoutUpdatedAt_CanBeUpdated()
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "PENDING_QC";
        batch.UpdatedAt = null;
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);
        _verification.Setup(x => x.UpdateAsync(7, null, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProductBatchReceivingUpdateResult.Updated);


        await Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, null, batch.CreatedAt),
            CancellationToken.None);
        _verification.Verify(x => x.UpdateAsync(7, null, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RepeatedReject_IsBlockedBeforeRepositoryWrite()
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "REJECTED";
        SetupBatch(batch);
        await Assert.ThrowsAsync<ConflictException>(() => Service().RejectAsync(7, 2, 11,
            new RejectProductBatchRequest("Damaged"), CancellationToken.None));
        _verification.Verify(x => x.RejectAsync(It.IsAny<long>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<BatchStatusHistory>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_QcReceiptBeforeReadOrDuringSave_ReturnsSpecificConflict(bool visibleAtRead)
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = "PENDING_QC";
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _query.Setup(x => x.HasQcInspectionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(visibleAtRead);
        _verification.Setup(x => x.UpdateAsync(7, null, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProductBatchReceivingUpdateResult.QcReceived);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateAsync(7, 11,
            new UpdateProductBatchReceivingRequest(100m, 100m, null, batch.CreatedAt), CancellationToken.None));
        Assert.Equal(ProductBatchService.QcReceivingLockMessage, exception.Message);
        _verification.Verify(x => x.UpdateAsync(7, null, batch.CreatedAt, 11,
            It.IsAny<VerifiedReceivingDetails>(), It.IsAny<CancellationToken>()),
            visibleAtRead ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData("PENDING_QC", false, true)]
    [InlineData("PENDING_QC", true, false)]
    [InlineData("APPROVED_FOR_STORAGE", false, false)]
    public async Task Detail_DerivesAvailabilityWithoutWriting(string status, bool qcExists, bool allowed)
    {
        var batch = SubmittedBatch();
        batch.BatchStatus = status;
        _query.Setup(x => x.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _query.Setup(x => x.HasQcInspectionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(qcExists);
        var result = await Service().GetAsync(7, CancellationToken.None);
        Assert.Equal(allowed, result.CanUpdateReceivingInformation);
        if (qcExists) Assert.Equal(ProductBatchService.QcReceivingLockMessage, result.ReceivingUpdateLockReason);
        else Assert.Equal(allowed, result.ReceivingUpdateLockReason is null);
        _verification.VerifyNoOtherCalls();
    }
}
