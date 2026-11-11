using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.SupplyContracts;

namespace SAW.Test.Application.SupplyContracts;

public sealed class SupplyContractServiceTests
{
    private readonly Mock<ISupplyContractRepository> _repository = new();
    private SupplyContractService Service => new(_repository.Object);

    private static CreateSupplyContractRequest CreateRequest => new(2, 3, 100, " week ", 25000,
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)), DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddMonths(1), " Terms ");

    [Fact]
    public async Task CreateNormalizesDeliveryPeriodAndTermsBeforeSaving()
    {
        await Service.CreateAsync(7, CreateRequest, default);
        _repository.Verify(x => x.CreateAsync(7, It.Is<CreateSupplyContractRequest>(r => r.DeliveryPeriod == "WEEK" && r.Terms == "Terms"
            && r.SupplierId == 2 && r.CropTypeId == 3 && r.DeliveryQuantityKg == 100 && r.UnitPriceVndPerKg == 25000), It.IsAny<CancellationToken>()), Times.Once);
    }

    public static IEnumerable<object[]> InvalidCreateRequests()
    {
        var request = CreateRequest;
        yield return [request with { SupplierId = 0 }];
        yield return [request with { CropTypeId = 0 }];
        yield return [request with { DeliveryQuantityKg = 0 }];
        yield return [request with { DeliveryQuantityKg = -1 }];
        yield return [request with { DeliveryPeriod = "DAY" }];
        yield return [request with { DeliveryPeriod = " " }];
        yield return [request with { UnitPriceVndPerKg = 0 }];
        yield return [request with { StartDate = request.StartDate.AddDays(-1) }];
        yield return [request with { EndDate = request.StartDate.AddDays(-1) }];
        yield return [request with { Terms = new string('a', 2001) }];
    }

    [Theory]
    [MemberData(nameof(InvalidCreateRequests))]
    public async Task InvalidCreateIsRejectedBeforeRepositoryWrite(CreateSupplyContractRequest request)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.CreateAsync(7, request, default));
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListNormalizesFiltersAndSortBeforeQuerying()
    {
        await Service.ListAsync(7, "WAREHOUSE_MANAGER", new("  Cam ", " approved ", SortBy: " EndDate ", SortDirection: " ASC "), default);
        _repository.Verify(x => x.ListAsync(7, "WAREHOUSE_MANAGER", It.Is<SupplyContractQuery>(q => q.Search == "Cam" && q.Status == "APPROVED"
            && q.SortBy == "enddate" && q.SortDirection == "asc"), It.IsAny<CancellationToken>()), Times.Once);
    }

    public static IEnumerable<object[]> InvalidListQueries()
    {
        yield return [new SupplyContractQuery(Page: 0)];
        yield return [new SupplyContractQuery(PageSize: 100)];
        yield return [new SupplyContractQuery(Search: new string('a', 101))];
        yield return [new SupplyContractQuery(Status: "INVALID")];
        yield return [new SupplyContractQuery(SortBy: "INVALID")];
        yield return [new SupplyContractQuery(SortDirection: "INVALID")];
    }

    [Theory]
    [MemberData(nameof(InvalidListQueries))]
    public async Task InvalidListFiltersNeverReachRepository(SupplyContractQuery query)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ListAsync(7, "SUPPLIER", query, default));
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("QC_STAFF")][InlineData("OPERATION_STAFF")][InlineData("DISTRIBUTOR")]
    public async Task UnsupportedActorsCannotReadContracts(string role)
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.ListAsync(7, role, new(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAsync(7, role, 1, default));
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingIdentityAndInvalidContractIdsAreRejected()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.ListAsync(0, "SUPPLIER", new(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.ConfirmBySupplierAsync(0, 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.GetAsync(7, "SUPPLIER", 0, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.CancelByAdminAsync(7, -1, default));
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EligibilityDefaultsToVietnamCalendarDateAndPreservesExplicitDate()
    {
        var before = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        await Service.GetEligibleForBatchAsync(7, null, default);
        var after = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        _repository.Verify(x => x.GetEligibleForBatchAsync(7, It.Is<DateOnly>(date => date >= before && date <= after), It.IsAny<CancellationToken>()), Times.Once);
        var explicitDate = before.AddDays(10);
        await Service.GetEligibleForBatchAsync(7, explicitDate, default);
        _repository.Verify(x => x.GetEligibleForBatchAsync(7, explicitDate, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("1234")]
    public async Task RejectionAndCancellationRequireReason(string? reason)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.RejectBySupplierAsync(7, 11, new(reason!), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.RejectByAdminAsync(7, 11, new(reason!), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.RejectCancellationByWarehouseAsync(7, 11, new(reason!), default));
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RejectionReasonsTrimAndEnforceMaximumLength()
    {
        await Service.RejectBySupplierAsync(7, 11, new("  Supplier reason  "), default);
        await Service.RejectByAdminAsync(7, 11, new("  Admin reason  "), default);
        _repository.Verify(x => x.RejectBySupplierAsync(7, 11, "Supplier reason", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.RejectByAdminAsync(7, 11, "Admin reason", It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<BadRequestException>(() => Service.RejectBySupplierAsync(7, 11, new(new string('a', 1001)), default));
    }

    [Fact]
    public async Task UpdateNormalizesContentAndRequiresSupplierApprovalAgainThroughRepository()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var request = new UpdateSupplyContractRequest(
            100, " week ", 25_000, today, today.AddMonths(3), "  Giao buổi sáng  ");
        _repository.Setup(x => x.UpdateByWarehouseAsync(
                7, 11, It.IsAny<UpdateSupplyContractRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupplyContractDetail)null!);

        await Service.UpdateByWarehouseAsync(7, 11, request, CancellationToken.None);

        _repository.Verify(x => x.UpdateByWarehouseAsync(
            7,
            11,
            It.Is<UpdateSupplyContractRequest>(value =>
                value.DeliveryPeriod == "WEEK"
                && value.Terms == "Giao buổi sáng"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SupplierCancellationRequestTrimsAndValidatesReason()
    {
        _repository.Setup(x => x.RequestCancellationBySupplierAsync(
                5, 9, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupplyContractDetail)null!);

        await Service.RequestCancellationBySupplierAsync(
            5, 9, new SupplyContractReasonRequest("  Không thể tiếp tục cung ứng  "), CancellationToken.None);

        _repository.Verify(x => x.RequestCancellationBySupplierAsync(
            5, 9, "Không thể tiếp tục cung ứng", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("APPROVED", true)]
    [InlineData("CANCELLATION_REJECTED", true)]
    [InlineData("PENDING_ADMIN", false)]
    [InlineData("PENDING_SUPPLIER", false)]
    [InlineData("PENDING_ADMIN_CANCELLATION", false)]
    [InlineData("CANCELLED", false)]
    public void SupplierCancellationIsAvailableOnlyAfterAdminApproval(string status, bool expected)
    {
        Assert.Equal(expected, SupplyContractCancellationPolicy.CanSupplierRequestCancellation(status));
    }

    [Fact]
    public async Task WarehouseCancellationRejectsShortReason()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.CancelByWarehouseAsync(
            7, 11, new SupplyContractReasonRequest("hủy"), CancellationToken.None));

        _repository.Verify(x => x.CancelByWarehouseAsync(
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRejectsContractThatWouldAlreadyBeExpired()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(-1);
        var request = new UpdateSupplyContractRequest(
            100, "WEEK", 25_000, yesterday.AddDays(-7), yesterday, null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            Service.UpdateByWarehouseAsync(7, 11, request, CancellationToken.None));

        _repository.Verify(x => x.UpdateByWarehouseAsync(
            It.IsAny<int>(), It.IsAny<long>(), It.IsAny<UpdateSupplyContractRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
