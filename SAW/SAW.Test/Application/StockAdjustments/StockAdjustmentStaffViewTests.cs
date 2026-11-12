using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.StockAdjustments;
using SAW.Domain.Common;

namespace SAW.Test.Application.StockAdjustments;

public sealed class StockAdjustmentStaffViewTests
{
    [Theory]
    [InlineData("PENDING_CUSTOMER", "PENDING_MANAGER")]
    [InlineData("CUSTOMER_ACCEPTED", "PENDING_MANAGER")]
    [InlineData("CUSTOMER_DECLINED", "PENDING_MANAGER")]
    [InlineData("ORDER_CANCELLED", "PENDING_MANAGER")]
    [InlineData("PENDING_MANAGER", "PENDING_MANAGER")]
    [InlineData("RESOLVED", "RESOLVED")]
    [InlineData("REPLACED", "REPLACED")]
    [InlineData(null, null)]
    public void StaffSummaryShowsWarehouseProgressWithoutCustomerNegotiationState(string? state, string? expected)
    {
        var summary = Summary(state);
        Assert.Equal(expected, StockAdjustmentStaffView.Summary(summary).WorkState);
        Assert.Equal(state, summary.WorkState);
    }

    private static AdjustmentSummary Summary(string? state = "CUSTOMER_ACCEPTED") => new(1, "ADJ-1", "COUNT", "COUNTING_ERROR", 7,
        "B-7", "Rice", "A", 100, 95, -5, "PENDING", "Staff", new(2026, 11, 1), state, "PO-9");
    private static AdjustmentDetail Detail() => new(Summary(), "Counted", null, null, null, [], [],
        new AdjustmentResolutionDetail("CUSTOMER_ACCEPTED", 2, new(9, 90, "PO-9", "RESERVED", 100, 1234567),
            [new(3, 1, "ACCEPTED", 100, 95, 1234567, 1000000, 1234567, 1000000, "Private customer negotiation", null, null, null, null)],
            null, null, 1, UpdatedBy: "Private manager identity"));

    [Fact]
    public void StaffDetailRemovesOrderPricesProposalsAndNegotiatorIdentity_WithoutChangingTheManagerProjection()
    {
        var source = Detail(); var before = JsonSerializer.Serialize(source);
        var staff = StockAdjustmentStaffView.Detail(source);
        Assert.Equal("PENDING_MANAGER", staff.Resolution!.State);
        Assert.Null(staff.Resolution.Order); Assert.Empty(staff.Resolution.Proposals); Assert.Null(staff.Resolution.UpdatedBy);
        Assert.Equal(source.Summary.ActualQuantity, staff.Summary.ActualQuantity);
        Assert.Equal(source.Resolution!.Version, staff.Resolution.Version);
        Assert.DoesNotContain("Private customer negotiation", JsonSerializer.Serialize(staff));
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Fact]
    public async Task OperationDetailEndpointActuallyAppliesTheStaffProjection()
    {
        var repository = new Mock<IStockAdjustmentRepository>(); var source = Detail();
        repository.Setup(r => r.GetAsync(1, default)).ReturnsAsync(source);
        var result = await new StockAdjustmentsController(new StockAdjustmentService(repository.Object)).Get(1, default);
        var response = Assert.IsType<ApiResponse<AdjustmentDetail>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(response.Data!.Resolution!.Order); Assert.Empty(response.Data.Resolution.Proposals);
        Assert.NotEmpty(source.Resolution!.Proposals);
    }

    [Fact]
    public void StaffListPreservesPaginationAndQuantitiesWhileHidingNegotiationStates()
    {
        var page = new AdjustmentPage<AdjustmentSummary>([Summary()], 30, 2, 20);
        var result = StockAdjustmentStaffView.Page(page);
        Assert.Equal(30, result.TotalCount); Assert.Equal(2, result.Page); Assert.Equal(20, result.PageSize);
        Assert.Equal("PENDING_MANAGER", Assert.Single(result.Items).WorkState);
        Assert.Equal(95, result.Items[0].ActualQuantity);
        Assert.Equal("CUSTOMER_ACCEPTED", page.Items[0].WorkState);
    }
}
