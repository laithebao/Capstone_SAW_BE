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
}
