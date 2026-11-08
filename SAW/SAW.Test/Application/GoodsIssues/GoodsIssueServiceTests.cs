using System.Text.Json;
using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsIssues;

namespace SAW.Test.Application.GoodsIssues;

public sealed class GoodsIssueServiceTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
    private static CreateGoodsIssueRequest Draft() => new(Guid.NewGuid(), 1, Today, " Receiver ", " Note ", [new(1, 10)]);
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(0.0001)] [InlineData(1.2345)] [InlineData(1000000000000000)]
    public void Invalid_weight_is_rejected_before_repository(decimal weight) => Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, null, null, [new(1, weight)]));
    [Theory]
    [InlineData("0.001")] [InlineData("100")] [InlineData("999999999999999.999")]
    public void Decimal_boundaries_are_accepted(string weight) => GoodsIssueRules.Draft(Today, null, null, [new(1, decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture))]);
    [Fact]
    public void Duplicate_empty_and_excessive_lines_are_rejected()
    {
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, null, null, []));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, null, null, [new(1, 1), new(1, 2)]));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, null, null, Enumerable.Range(1, 101).Select(i => new GoodsIssueLineRequest(i, 1)).ToList()));
    }
    [Fact]
    public void Date_and_text_boundaries()
    {
        GoodsIssueRules.Draft(Today, new string('a', 200), new string('b', 1000), [new(1, 1)]);
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today.AddDays(1), null, null, [new(1, 1)]));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(default, null, null, [new(1, 1)]));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, new string('a', 201), null, [new(1, 1)]));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Draft(Today, null, new string('a', 1001), [new(1, 1)]));
    }
    [Theory]
    [InlineData(0, 20)] [InlineData(100001, 20)] [InlineData(1, 0)] [InlineData(1, 101)]
    public void Invalid_pagination(int page, int size) => Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(Page: page, PageSize: size)));
    [Fact]
    public void Query_whitelist_and_boundaries()
    {
        Assert.Equal("term", GoodsIssueRules.Query(new(Search: " term ")).Search);
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(Status: "ISSUED")));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(SortBy: "DROP TABLE")));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(WarehouseLocationId: 0)));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(Search: new string('a', 101))));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(Search: "a\nb")));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(FromDate: Today, ToDate: Today.AddDays(-1))));
        Assert.Throws<BadRequestException>(() => GoodsIssueRules.Query(new(ToDate: DateOnly.MaxValue)));
    }
    [Fact]
    public async Task Service_normalizes_optional_text_and_preserves_reservation_input()
    {
        var repo = new Mock<IGoodsIssueRepository>(MockBehavior.Strict);
        var input = Draft();
        repo.Setup(r => r.CreateAsync(It.Is<CreateGoodsIssueRequest>(v => v.ReceiverName == "Receiver" && v.Note == "Note" && v.Lines == input.Lines), 12, default)).ReturnsAsync(99);
        repo.Setup(r => r.GetAsync(99, default)).ReturnsAsync((GoodsIssueDetail)null!);
        await new GoodsIssueService(repo.Object).CreateAsync(input, 12, default);
        repo.VerifyAll();
        repo.Reset();
        repo.Setup(r => r.CreateAsync(It.Is<CreateGoodsIssueRequest>(v => v.ReceiverName == null && v.Note == null), 12, default)).ReturnsAsync(99);
        repo.Setup(r => r.GetAsync(99, default)).ReturnsAsync((GoodsIssueDetail)null!);
        await new GoodsIssueService(repo.Object).CreateAsync(input with { ReceiverName = " ", Note = " " }, 12, default);
        repo.VerifyAll();
    }
    [Fact]
    public async Task Missing_actor_snapshot_or_operation_key_never_calls_repository()
    {
        var repo = new Mock<IGoodsIssueRepository>(MockBehavior.Strict); var service = new GoodsIssueService(repo.Object);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(Draft(), 0, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(Draft() with { RequestId = Guid.Empty }, 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.ConfirmAsync(1, new(Guid.NewGuid(), "bad"), 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateDraftAsync(1, new(Guid.NewGuid(), "", Today, null, null, [new(1, 1)]), 1, default));
        repo.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("operationAccountId", "123")]
    [InlineData("issueStatus", "\"COMMITTED\"")]
    [InlineData("quantityOnHand", "1000")]
    public void DTO_does_not_accept_actor_status_or_stock(string name, string value)
    {
        var json = JsonSerializer.Serialize(Draft(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json = json[..^1] + ",\"" + name + "\":" + value + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateGoodsIssueRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
