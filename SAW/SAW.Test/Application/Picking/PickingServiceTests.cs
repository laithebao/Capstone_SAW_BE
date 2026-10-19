using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Application.Features.Picking;

namespace SAW.Test.Application.Picking;

public sealed class PickingServiceTests
{
    private static ConfirmPickingRequest Request() => new(Guid.NewGuid(), 1, 2, 3, 4, 1, 0, " note ");
    [Theory]
    [InlineData(0,20)] [InlineData(1,0)] [InlineData(1,101)] [InlineData(100001,20)]
    public async Task Invalid_pagination_never_queries_repository(int page, int size)
    {
        var repo = new Mock<IPickingRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<BadRequestException>(() => new PickingService(repo.Object).SearchAsync(new(Page: page, PageSize: size), default));
        repo.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("status")] [InlineData("sort")] [InlineData("keyword")] [InlineData("dates")] [InlineData("maxdate")] [InlineData("mindate")]
    public async Task Unsupported_or_unbounded_filters_do_not_reach_SQL(string input)
    {
        var query = input switch { "status" => new PickingQuery(Status: "PICKED"), "sort" => new(Sort: "id;drop table"),
            "keyword" => new(Search: new string('x',101)), "dates" => new(FromDate: new(2026,2,1),ToDate:new(2026,1,1)),
            "mindate" => new(FromDate:DateOnly.MinValue), _ => new(ToDate:DateOnly.MaxValue) };
        await Assert.ThrowsAsync<BadRequestException>(() => new PickingService(Mock.Of<IPickingRepository>()).SearchAsync(query, default));
    }
    [Fact]
    public async Task Search_normalizes_supported_filters_without_changing_page_or_sort()
    {
        var repo = new Mock<IPickingRepository>();
        repo.Setup(r => r.SearchAsync(It.IsAny<PickingQuery>(), default)).ReturnsAsync(new PickingPage<PickingOrderSummary>([], 0, 2, 100));
        await new PickingService(repo.Object).SearchAsync(new(" rice ", "reserved", "delivery_asc", 2, 100), default);
        var expected = new PickingQuery("rice", "RESERVED", "delivery_asc", 2, 100);
        repo.Verify(r => r.SearchAsync(expected, default), Times.Once);
    }
    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("0.0001")] [InlineData("1000000000000000")]
    public async Task Invalid_kg_is_rejected_before_write(string raw)
    {
        var repo = new Mock<IPickingRepository>(MockBehavior.Strict);
        var request = Request() with { QuantityKg = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) };
        await Assert.ThrowsAsync<BadRequestException>(() => new PickingService(repo.Object).ConfirmAsync(1, request, 1, default));
        repo.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("key")] [InlineData("reservation")] [InlineData("line")] [InlineData("inventory")] [InlineData("batch")]
    [InlineData("snapshot")] [InlineData("note")]
    public async Task Invalid_identity_snapshot_and_note_never_write(string kind)
    {
        var request = Request();
        request = kind switch { "key" => request with { RequestId = Guid.Empty }, "reservation" => request with { ReservationId = 0 },
            "line" => request with { OrderDetailId = 0 }, "inventory" => request with { InventoryId = 0 },
            "batch" => request with { ProductBatchId = 0 }, "snapshot" => request with { ExpectedPickedQuantityKg = -1 },
            _ => request with { Note = new string('n', 1001) } };
        await Assert.ThrowsAsync<BadRequestException>(() => new PickingService(Mock.Of<IPickingRepository>()).ConfirmAsync(1, request, 1, default));
    }
    [Fact]
    public async Task Confirm_uses_actor_argument_trims_note_and_reloads_trigger_results()
    {
        var repo = new Mock<IPickingRepository>(); var req = Request();
        var detail = new PickingOrderDetail(new(1,"O","D",DateTime.UtcNow,null,"PARTIALLY_PICKED",100,100,1,true,null),[]);
        repo.Setup(r => r.GetAsync(1, default)).ReturnsAsync(detail);
        Assert.Same(detail, await new PickingService(repo.Object).ConfirmAsync(1,req,9,default));
        var expected = req with { Note = "note" };
        repo.Verify(r => r.ConfirmAsync(1, expected, 9, default),Times.Once);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PickingService(repo.Object).ConfirmAsync(1,req,0,default));
    }
    [Theory]
    [InlineData("pickedByAccountId")] [InlineData("orderStatus")] [InlineData("pickedQuantity")] [InlineData("role")]
    public void Client_cannot_bind_server_owned_values(string field)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Request(),options).TrimEnd('}') + $",\"{field}\":\"forged\"}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ConfirmPickingRequest>(json, options));
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, typeof(ConfirmPickingRequest).GetCustomAttributes(typeof(JsonUnmappedMemberHandlingAttribute),false)
            .Cast<JsonUnmappedMemberHandlingAttribute>().Single().UnmappedMemberHandling);
    }
    [Fact]
    public void Staff_endpoints_and_manager_allocator_keep_separate_roles()
    {
        Assert.Equal("OPERATION_STAFF", typeof(PickingOrdersController).GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles);
        Assert.Equal("WAREHOUSE_MANAGER", typeof(WarehouseOrdersController).GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles);
    }
    [Theory]
    [InlineData("REJECTED")] [InlineData("QUARANTINE")] [InlineData("CANCELLED")] [InlineData("ISSUED")]
    public void Blocked_batch_states_are_independent_of_grade(string status) => Assert.NotNull(PickingRules.BatchLockReason(status,null));
    [Fact]
    public void Expiry_today_is_usable_but_yesterday_is_not()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        Assert.Null(PickingRules.BatchLockReason("IN_STOCK",today));
        Assert.NotNull(PickingRules.BatchLockReason("IN_STOCK",today.AddDays(-1)));
    }
}
