using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.StockAdjustments;

namespace SAW.Test.Application.StockAdjustments;

public sealed class StockAdjustmentReviewServiceTests
{
    private static ReviewStockAdjustmentRequest Input(string decision="APPROVE",string? note=null)=>new(Guid.NewGuid(),decision,note,new string('A',64));
    [Theory]
    [InlineData("REJECT",null)] [InlineData("REJECT","  ")] [InlineData("COMMITTED","note")] [InlineData("approve",null)]
    public async Task Invalid_decisions_and_blank_rejection_reasons_never_reach_repository(string decision,string? note)
    {
        var repo=new Mock<IStockAdjustmentRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<BadRequestException>(()=>new StockAdjustmentReviewService(repo.Object).ReviewAsync(1,Input(decision,note),1,default));repo.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Invalid_ids_actor_key_snapshot_and_note_length_are_rejected()
    {
        var service=new StockAdjustmentReviewService(new Mock<IStockAdjustmentRepository>(MockBehavior.Strict).Object);
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ReviewAsync(0,Input(),1,default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReviewAsync(1,Input(),0,default));
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ReviewAsync(1,Input() with{RequestId=Guid.Empty},1,default));
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ReviewAsync(1,Input() with{ExpectedSnapshot="old"},1,default));
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ReviewAsync(1,Input(note:new string('a',1001)),1,default));
    }
    [Theory]
    [InlineData("APPROVE","  ",null)] [InlineData("APPROVE"," note ","note")] [InlineData("REJECT"," reason ","reason")]
    public async Task Review_note_is_trimmed_and_empty_approval_note_is_null(string decision,string? note,string? expected)
    {
        var repo=new Mock<IStockAdjustmentRepository>();var request=Input(decision,note);
        await new StockAdjustmentReviewService(repo.Object).ReviewAsync(1,request,2,default);
        repo.Verify(r=>r.ReviewAsync(1,It.Is<ReviewStockAdjustmentRequest>(x=>x.ReviewNote==expected&&x.Decision==decision&&x.RequestId==request.RequestId),2,default),Times.Once);
        repo.Verify(r=>r.GetReviewAsync(1,default),Times.Once);
    }
}
