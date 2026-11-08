using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.SupplyContracts;

namespace SAW.Test.Application.SupplyContracts;

public sealed class SupplyContractServiceTests
{
    private readonly Mock<ISupplyContractRepository> _repository = new();
    private SupplyContractService Service => new(_repository.Object);

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
