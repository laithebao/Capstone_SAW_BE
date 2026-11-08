using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SAW.Test.Infrastructure.DistributorOrders;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [DistributorBrowserFact]
    public async Task Browser_manager_reviews_count_disposal_reject_and_stale_form_using_real_API_SQL()
    {
        var count=await Scenario();var disposal=await Scenario([40,60,0],true);var stale=await Scenario();var rejected=await Scenario();
        var a=await Service().CreateAsync(await Request(count.Batch),F.ActorId,default);var b=await Service().CreateAsync(await Request(disposal.Batch,"DISPOSAL"),F.ActorId,default);
        var c=await Service().CreateAsync(await Request(stale.Batch),F.ActorId,default);var d=await Service().CreateAsync(await Request(rejected.Batch,"DISPOSAL"),F.ActorId,default);
        var actor=await Manager();using var api=await F.ApiAsync();var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Capstone_SAW_FE")))root=root.Parent;Assert.NotNull(root);
        var evidence=Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY")??Path.Combine(root.FullName,"Capstone_SAW_BE","test-results","stock-adjustment-review");Directory.CreateDirectory(evidence);
        var start=new ProcessStartInfo("node"){WorkingDirectory=Path.Combine(root.FullName,"Capstone_SAW_FE"),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("tests/stock-adjustment-review-browser.mjs");start.Environment["SAW_TEST_API_URL"]=api.BaseAddress!.ToString();start.Environment["SAW_TEST_TOKEN"]=Token(F.ActorId,"OPERATION_STAFF");start.Environment["SAW_TEST_MANAGER_TOKEN"]=Token(actor,"WAREHOUSE_MANAGER");
        start.Environment["SAW_TEST_COUNT_REQUEST_ID"]=a.Summary.Id.ToString();start.Environment["SAW_TEST_DISPOSAL_REQUEST_ID"]=b.Summary.Id.ToString();start.Environment["SAW_TEST_STALE_REQUEST_ID"]=c.Summary.Id.ToString();start.Environment["SAW_TEST_REJECT_REQUEST_ID"]=d.Summary.Id.ToString();start.Environment["SAW_TEST_EVIDENCE_DIRECTORY"]=evidence;
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token);}catch{process.Kill(true);throw;}
        var log=await output+await error;await File.WriteAllTextAsync(Path.Combine(evidence,"stock-adjustment-review-browser.log"),log);Assert.True(process.ExitCode==0,log);
        await using var db=F.Context();Assert.Equal(95,await db.Inventories.Where(i=>i.ProductBatchId==count.Batch).Select(i=>i.QuantityOnHand).SingleAsync());
        Assert.All(await db.Inventories.Where(i=>i.ProductBatchId==disposal.Batch).ToListAsync(),i=>Assert.Equal(0,i.QuantityOnHand));Assert.Equal("DISPOSED",await db.ProductBatches.Where(x=>x.ProductBatchId==disposal.Batch).Select(x=>x.BatchStatus).SingleAsync());
        Assert.Equal("REJECTED",(await ReadEntity(d.Summary.Id)).AdjustmentStatus);Assert.Equal(100,await db.Inventories.Where(i=>i.ProductBatchId==rejected.Batch).Select(i=>i.QuantityOnHand).SingleAsync());Assert.Equal("REJECTED",(await ReadEntity(c.Summary.Id)).AdjustmentStatus);
    }
}
