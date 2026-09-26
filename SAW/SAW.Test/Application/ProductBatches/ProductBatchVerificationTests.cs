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
        BatchStatus = "SUBMITTED"
    };

    private void SetupBatch(ProductBatch? batch)
    {
        _query.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _verification.Setup(x => x.ConfirmAsync(7, 2, It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task SubmittedBatch_IsVerifiedWithoutChangingDeclarationOrCreatingAnotherBatch()
    {
        var batch = SubmittedBatch();
        SetupBatch(batch);

        var result = await Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None);

        Assert.Equal(7, result.Id);
        Assert.Equal("BATCH001", result.BatchCode);
        Assert.Equal(940m, result.VerifiedQuantity);
        Assert.Equal(940m, result.VerifiedWeightInKg);
        Assert.Equal("PENDING_QC", result.BatchStatus);
        Assert.Equal(1000m, batch.DeclaredQuantity);
        Assert.Equal(1000m, batch.WeightInKg);
        Assert.Null(batch.VerifiedQuantity);
        Assert.Null(batch.VerifiedWeightInKg);
        _verification.Verify(x => x.ConfirmAsync(7, 2, 940m, 940m,
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
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<BatchStatusHistory>(),
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
        _verification.Verify(x => x.ConfirmAsync(7, 2, 1040m, 1040m,
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentVerification_IsRejected()
    {
        SetupBatch(SubmittedBatch());
        _verification.Setup(x => x.ConfirmAsync(7, 2, 940m, 940m,
            It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ConflictException>(() => Service().VerifyAsync(7, 2, 11,
            new VerifyProductBatchRequest(940m, 940m), CancellationToken.None));
    }
}
