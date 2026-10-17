using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SAW.Infrastructure.QrCodes;
using SAW.Test.Infrastructure.DistributorOrders;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [DistributorBrowserFact]
    public async Task Browser_count_disposal_conflict_images_and_role_guard_use_real_API_SQL()
    {
        var count=await Scenario();var disposal=await Scenario([40,60,0],true);var stale=await Scenario();
        var beforeCount=await State(count.Batch);var beforeDisposal=await State(disposal.Batch);
        using var api=await F.ApiAsync();var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Capstone_SAW_FE")))root=root.Parent;
        Assert.NotNull(root);var evidence=Environment.GetEnvironmentVariable("SAW_TEST_EVIDENCE_DIRECTORY")??Path.Combine(root.FullName,"Capstone_SAW_BE","test-results","stock-adjustments");Directory.CreateDirectory(evidence);
        var png=Path.Combine(evidence,"stock-adjustment-test-image.png");await File.WriteAllBytesAsync(png,new PngQrCodeRenderer().RenderPng("https://test.invalid/evidence"));
        var start=new ProcessStartInfo("node"){WorkingDirectory=Path.Combine(root.FullName,"Capstone_SAW_FE"),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("tests/stock-adjustments-browser.mjs");start.Environment["SAW_TEST_API_URL"]=api.BaseAddress!.ToString();start.Environment["SAW_TEST_COUNT_BATCH_ID"]=count.Batch.ToString();start.Environment["SAW_TEST_DISPOSAL_BATCH_ID"]=disposal.Batch.ToString();start.Environment["SAW_TEST_STALE_BATCH_ID"]=stale.Batch.ToString();
        start.Environment["SAW_TEST_TOKEN"]=Token(F.ActorId,"OPERATION_STAFF");start.Environment["SAW_TEST_MANAGER_TOKEN"]=Token(F.ActorId,"WAREHOUSE_MANAGER");start.Environment["SAW_TEST_IMAGE_PATH"]=png;start.Environment["SAW_TEST_EVIDENCE_DIRECTORY"]=evidence;
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token);}catch{process.Kill(true);throw;}
        var log=await output+await error;await File.WriteAllTextAsync(Path.Combine(evidence,"stock-adjustments-browser.log"),log);Assert.True(process.ExitCode==0,log);
        Assert.Equal(beforeCount,await State(count.Batch));Assert.Equal(beforeDisposal,await State(disposal.Batch));
        await using var db=F.Context();Assert.Equal(1,await db.StockAdjustments.CountAsync(a=>a.Inventory.ProductBatchId==disposal.Batch));
    }
}
