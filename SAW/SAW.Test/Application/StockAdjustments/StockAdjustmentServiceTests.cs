using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Application.StockAdjustments;

public sealed class StockAdjustmentServiceTests
{
    private static CreateStockAdjustmentRequest Input() => new(Guid.NewGuid(), 1, "COUNT", 2, 95, "COUNTING_ERROR", " Counted ", new string('A', 64), []);
    [Theory]
    [InlineData(-1)] [InlineData(0.0001)] [InlineData(1000000000000000)]
    public void Invalid_decimal_count_is_rejected(decimal amount) => Assert.Throws<BadRequestException>(() => StockAdjustmentRules.Create(Input() with { ActualQuantityKg = amount }, 1));
    [Theory]
    [InlineData("0")] [InlineData("0.001")] [InlineData("999999999999999.999")]
    public void Decimal_boundary_count_is_valid(string input) { var amount=decimal.Parse(input,System.Globalization.CultureInfo.InvariantCulture); Assert.Equal(amount, StockAdjustmentRules.Create(Input() with { ActualQuantityKg = amount }, 1).ActualQuantityKg); }
    [Theory]
    [InlineData(0,20)] [InlineData(1,0)] [InlineData(1,101)] [InlineData(100001,20)]
    public void Invalid_page_is_rejected(int page,int size) => Assert.Throws<BadRequestException>(() => StockAdjustmentRules.Query(new(Page: page, PageSize: size)));
    [Fact]
    public void Whitelist_trim_reason_null_counts_and_disposal_types_are_enforced()
    {
        Assert.Equal("Counted", StockAdjustmentRules.Create(Input(), 1).Reason);
        foreach (var r in new[] { Input() with { Reason="   " }, Input() with { Reason=new string('x',1001) }, Input() with { RequestKind="APPROVE" },
            Input() with { ActualQuantityKg=null }, Input() with { RequestKind="DISPOSAL" }, Input() with { InventoryId=null },
            Input() with { ImageIds=Enumerable.Repeat(Guid.NewGuid(),2).ToArray() }, Input() with { ImageIds=Enumerable.Range(0,6).Select(_=>Guid.NewGuid()).ToArray() } })
            Assert.Throws<BadRequestException>(() => StockAdjustmentRules.Create(r,1));
        var disposal=Input() with { RequestKind="DISPOSAL",InventoryId=null,ActualQuantityKg=null,AdjustmentType="SPOILAGE" };
        Assert.Null(StockAdjustmentRules.Create(disposal,1).ActualQuantityKg);
        Assert.Throws<BadRequestException>(() => StockAdjustmentRules.Create(disposal with { AdjustmentType="COUNTING_ERROR" },1));
        Assert.Throws<UnauthorizedAccessException>(()=>StockAdjustmentRules.Create(Input(),0));
    }
    [Fact]
    public void Invalid_filters_do_not_reach_repository()
    {
        var mock=new Mock<IStockAdjustmentRepository>(MockBehavior.Strict); var service=new StockAdjustmentService(mock.Object);
        foreach(var q in new[]{new StockAdjustmentQuery(SortBy:"SQL"),new StockAdjustmentQuery(Status:"COMMITTED"),new StockAdjustmentQuery(Search:new string('a',101)),new StockAdjustmentQuery(WarehouseLocationId:-1),new StockAdjustmentQuery(FromDate:new(2026,5,1),ToDate:new(2026,4,1))})
            Assert.Throws<BadRequestException>(()=>{_ = service.SearchAsync(q,default);});
        mock.VerifyNoOtherCalls();
    }
    [Fact]
    public void Forged_image_header_or_truncated_content_is_not_an_image()
    {
        Assert.Throws<BadRequestException>(()=>StockAdjustmentRepository.ValidateImage("not an image"u8.ToArray()));
        Assert.Throws<BadRequestException>(()=>StockAdjustmentRepository.ValidateImage([137,80,78,71,13,10,26,10,0,0]));
        Assert.Throws<BadRequestException>(()=>StockAdjustmentRepository.ValidateImage(new byte[10485761]));
    }

    [Fact]
    public async Task Create_PreservesActorSnapshotAndReplacementId_AndNormalizesEvidenceBeforeSaving()
    {
        var repository = new Mock<IStockAdjustmentRepository>(MockBehavior.Strict);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var request = Input() with { ImageIds = new[] { second, first }, ReplacesStockAdjustmentId = 8 };
        var expected = new AdjustmentDetail(new(99, "ADJ-99", "COUNT", "COUNTING_ERROR", 1, "B-1", "Rice", "A", 100, 95, -5, "PENDING", "Staff", DateTime.UtcNow), "Counted", null, null, null, [], []);
        var sequence = new MockSequence();
        repository.InSequence(sequence).Setup(r => r.CreateAsync(It.Is<CreateStockAdjustmentRequest>(x => x.RequestId == request.RequestId
            && x.ExpectedSnapshot == request.ExpectedSnapshot && x.ReplacesStockAdjustmentId == 8 && x.Reason == "Counted"
            && x.ImageIds.SequenceEqual(new[] { first, second }) && x.ActualQuantityKg == 95), 7, default)).ReturnsAsync(99);
        repository.InSequence(sequence).Setup(r => r.GetAsync(99, default)).ReturnsAsync(expected);
        var result = await new StockAdjustmentService(repository.Object).CreateAsync(request, 7, default);
        Assert.Same(expected, result); Assert.Equal("PENDING", result.Summary.Status);
        Assert.Equal(new[] { second, first }, request.ImageIds);
        repository.VerifyAll();
    }

    [Fact]
    public async Task StaleScope_CreateConflictDoesNotReadBackAFalseSuccess()
    {
        var repository = new Mock<IStockAdjustmentRepository>(MockBehavior.Strict);
        repository.Setup(r => r.CreateAsync(It.IsAny<CreateStockAdjustmentRequest>(), 7, default)).ThrowsAsync(new ConflictException("Scope changed"));
        await Assert.ThrowsAsync<ConflictException>(() => new StockAdjustmentService(repository.Object).CreateAsync(Input(), 7, default));
        repository.Verify(r => r.GetAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EvidenceOperations_RejectMissingActorBeforeAccessingStorage(int actor)
    {
        var repository = new Mock<IStockAdjustmentRepository>(MockBehavior.Strict); var service = new StockAdjustmentService(repository.Object);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UploadAsync([1], actor, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveUploadAsync(Guid.NewGuid(), actor, default));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidEvidenceSizeAndEmptyImageId_NeverReachStorage()
    {
        var repository = new Mock<IStockAdjustmentRepository>(MockBehavior.Strict); var service = new StockAdjustmentService(repository.Object);
        await Assert.ThrowsAsync<BadRequestException>(() => service.UploadAsync([], 7, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.UploadAsync(new byte[10485761], 7, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.RemoveUploadAsync(Guid.Empty, 7, default));
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("COUNT", null)]
    [InlineData("COUNT", 2L)]
    [InlineData("DISPOSAL", null)]
    public async Task ValidScopeRetainsRequestKindAndSelectedInventory(string kind, long? inventoryId)
    {
        var repository = new Mock<IStockAdjustmentRepository>();
        await new StockAdjustmentService(repository.Object).ScopeAsync(1, kind, inventoryId, default);
        repository.Verify(r => r.ScopeAsync(1, kind, inventoryId, default), Times.Once);
    }

    [Theory]
    [InlineData("DISPOSAL", 2L)]
    [InlineData("COUNT", 0L)]
    [InlineData("DISPOSE", null)]
    public async Task InvalidScopeNeverQueriesInventory(string kind, long? inventoryId)
    {
        var repository = new Mock<IStockAdjustmentRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<BadRequestException>(() => new StockAdjustmentService(repository.Object).ScopeAsync(1, kind, inventoryId, default));
        repository.VerifyNoOtherCalls();
    }
}
