using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Services;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Interfaces;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Repositories;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.QrCodes;

namespace SAW.Test.Application.ProductBatches;

public sealed class ReceivingWorkflowBoundaryTests
{
    private static ProductBatch Batch() => new() { ProductBatchId = 7, SupplierId = 2, BatchCode = "R2",
        BatchStatus = "SUBMITTED", CreatedAt = new DateTime(2026, 1, 1), ProductName = "Rice",
        Supplier = new() { SupplierName = "Supplier" }, CropType = new() { CropName = "Rice" },
        GrowingArea = new() { AreaName = "Farm" } };
    private static (ProductBatchService Service, Mock<IProductBatchQueryRepository> Read, Mock<IProductBatchVerificationRepository> Write) Setup()
    {
        var read = new Mock<IProductBatchQueryRepository>(); var write = new Mock<IProductBatchVerificationRepository>();
        read.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Batch());
        read.Setup(r => r.GetRelatedInformationAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(ProductBatchRelatedInformation.Empty);
        write.Setup(w => w.ConfirmAsync(7, 2, It.IsAny<VerifiedReceivingDetails>(), It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        write.Setup(w => w.RejectAsync(7, 2, It.IsAny<string>(), It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return (new(read.Object, write.Object), read, write);
    }
    [Fact]
    public async Task UC26_keyword_exactly_50_is_valid()
    {
        var (service, read, _) = Setup();
        read.Setup(r => r.SearchAsync(It.IsAny<ProductBatchQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((Array.Empty<ProductBatch>(), 0));
        await service.SearchAsync(new(new string('x', 50), null, null, null, null), default);
        read.Verify(r => r.SearchAsync(It.Is<ProductBatchQuery>(q => q.BatchCode!.Length == 50), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task UC27_decimal_and_text_boundaries_are_valid_and_excess_is_rejected()
    {
        var (service, _, write) = Setup();
        foreach(var amount in new[] { 0.001m, 999999999999999.999m })
            await service.VerifyAsync(7, 2, 1, new(amount, amount, new string('p',100), 1, amount, new string('n',1000)), default);
        await service.RejectAsync(7, 2, 1, new("  " + new string('r',1000) + "  "), default);
        write.Verify(w => w.RejectAsync(7, 2, It.Is<string>(r => r.Length == 1000), It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<BadRequestException>(() => service.VerifyAsync(7, 2, 1, new(1000000000000000m,1), default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.VerifyAsync(7, 2, 1, new(1,1,new string('p',101)), default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.VerifyAsync(7, 2, 1, new(1,1,ReceivingNote:new string('n',1001)), default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.RejectAsync(7, 2, 1, new(new string('r',1001)), default));
        write.Verify(w => w.ConfirmAsync(7, 2, It.IsAny<VerifiedReceivingDetails>(), It.IsAny<BatchStatusHistory>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Fact]
    public async Task UC28_invalid_optional_and_amount_boundaries_never_write()
    {
        var (service, _, write) = Setup();
        var version = new DateTime(2026,1,1);
        var invalid = new[] { new UpdateProductBatchReceivingRequest(0,1,null,version), new(1,0,null,version),
            new(1,1,null,version,new string('p',101)), new(1,1,null,version,VerifiedPackageCount:0),
            new(1,1,null,version,VerifiedPackageUnitWeightKg:1.0001m), new(1,1,null,version,ReceivingNote:new string('n',1001)) };
        foreach(var request in invalid) await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateAsync(7,1,request,default));
        write.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("QC_STAFF")][InlineData("WAREHOUSE_MANAGER")][InlineData("ADMINISTRATOR")]
    public async Task UC29_controller_read_role_does_not_inherit_receiving_write_permission(string role)
    {
        var (service, read, _) = Setup(); var batch = Batch(); batch.BatchStatus = "PENDING_QC";
        read.Setup(r => r.GetWarehouseByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        var editable = await service.GetAsync(7,default); Assert.True(editable.CanUpdateReceivingInformation);
        var mock = new Mock<IProductBatchService>(); mock.Setup(s => s.GetAsync(7,It.IsAny<CancellationToken>())).ReturnsAsync(editable);
        var controller = new ProductBatchesController(mock.Object) { ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role,role)],"test")) } } };
        var response = await controller.Get(7, default);
        var wrapper = Assert.IsType<ApiResponse<ProductBatchDetail>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.False(wrapper.Data!.CanUpdateReceivingInformation); Assert.NotNull(wrapper.Data.ReceivingUpdateLockReason);
    }
    private static GoodsReceiptDetail Receipt() => new(7,"GR",1,"B","Rice",2,"Supplier",10,"Box",100,2,"A",
        new DateOnly(2026,1,1),"DRAFT",null,null,"Staff",new(2,new DateTime(2026,1,1),null));
    [Fact]
    public async Task UC30_keyword100_and_max_date_boundary()
    {
        var read = new Mock<IGoodsReceiptQueryRepository>(); var write = new Mock<IGoodsReceiptRepository>();
        read.Setup(r => r.SearchAsync(It.IsAny<GoodsReceiptQuery>(),It.IsAny<CancellationToken>())).ReturnsAsync(new GoodsReceiptPage<GoodsReceiptDetail>([],0,1,10));
        var service = new GoodsReceiptService(read.Object,write.Object);
        await service.SearchAsync(new(Search:new string('x',100)),default);
        await Assert.ThrowsAsync<BadRequestException>(() => service.SearchAsync(new(ToDate:DateOnly.MaxValue),default));
        read.Verify(r => r.SearchAsync(It.IsAny<GoodsReceiptQuery>(),It.IsAny<CancellationToken>()),Times.Once); write.VerifyNoOtherCalls();
    }
    [Fact]
    public void UC31_capacity_exact_boundary_and_null_limit()
    {
        GoodsReceiptRules.EnsureCapacity(100,50,150,"Location"); GoodsReceiptRules.EnsureCapacity(100,50,null,"Location");
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureCapacity(100,50,149,"Location"));
    }
    [Fact]
    public async Task UC31_create_and_edit_today_with_note1000()
    {
        var read=new Mock<IGoodsReceiptQueryRepository>();var write=new Mock<IGoodsReceiptRepository>();
        read.Setup(r=>r.GetAsync(7,It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());
        write.Setup(r=>r.CreateAsync(It.IsAny<CreateGoodsReceiptRequest>(),1,It.IsAny<CancellationToken>())).ReturnsAsync(7L);
        var service=new GoodsReceiptService(read.Object,write.Object);var today=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var note=new string('n',1000);
        await service.CreateAsync(new(1,2,today," "+note+" "),1,default);
        await service.UpdateDraftAsync(7,new(2,today," "+note+" ",Receipt().Snapshot),1,default);
        write.Verify(r=>r.CreateAsync(It.Is<CreateGoodsReceiptRequest>(q=>q.Note==note&&q.ReceivedDate==today),1,It.IsAny<CancellationToken>()),Times.Once);
        write.Verify(r=>r.UpdateDraftAsync(7,It.Is<UpdateGoodsReceiptDraftRequest>(q=>q.Note==note&&q.ReceivedDate==today),1,It.IsAny<CancellationToken>()),Times.Once);
    }
    [Fact]
    public async Task UC55_worker_retries_same_batch_in_later_sweep()
    {
        var read=new Mock<IQrCodeRepository>();read.Setup(r=>r.GetCandidatesAsync(0,1,It.IsAny<CancellationToken>())).ReturnsAsync(new long[]{1});
        read.Setup(r=>r.GetCandidatesAsync(1,1,It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<long>());
        var storage=new Mock<IQrImageStorage>();storage.SetupGet(s=>s.IsConfigured).Returns(true);
        var service=new Mock<IProductBatchQrCodeService>();var attempts=0;
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(s=>s.GenerateAsync(1,It.IsAny<CancellationToken>())).Returns(()=>{if(Interlocked.Increment(ref attempts)==1)throw new HttpRequestException("fixture failure");done.TrySetResult();return Task.CompletedTask;});
        using var provider=new ServiceCollection().AddScoped(_=>read.Object).AddScoped(_=>storage.Object).AddScoped(_=>service.Object).BuildServiceProvider();
        using var worker=new QrCodeGenerationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new QrCodeOptions{PublicFrontendBaseUrl="https://receiving.test.invalid",BatchSize=1,IntervalSeconds=5},NullLogger<QrCodeGenerationWorker>.Instance);
        await worker.StartAsync(default);try{await done.Task.WaitAsync(TimeSpan.FromSeconds(25));}finally{await worker.StopAsync(default);}
        service.Verify(s=>s.GenerateAsync(1,It.IsAny<CancellationToken>()),Times.Exactly(2));
        read.Verify(r=>r.GetCandidatesAsync(1,1,It.IsAny<CancellationToken>()),Times.Once);
    }
}
