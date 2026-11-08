using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Application.Features.StockAdjustments;
using SAW.Infrastructure.QrCodes;
using SAW.Infrastructure.Repositories;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed class StockAdjustmentMigrationTests
{
    [ReceivingSqlFact]
    public async Task Legacy_slip_is_preserved_through_Up_repeated_Up_and_Down_and_read_as_legacy_snapshot()
    {
        var f=new ReceivingSqlFixture(useMigrationBaseline:true);await f.InitializeAsync();
        try
        {
            await using var db=f.Context();
            await db.GetService<IMigrator>().MigrateAsync("20261016153240_FixGoodsIssueWorkflowTriggers");
            var batch=await f.BatchAsync();
            var stock=new Inventory {ProductBatchId=batch.ProductBatchId,QuantityOnHand=100,Unit="kg",LastUpdatedAt=DateTime.UtcNow,
                WarehouseLocation=new WarehouseLocation {LocationCode="LEGACY-ADJUSTMENT",ZoneName="Fixture",LocationStatus="ACTIVE"}};
            db.Add(stock);await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO STOCK_ADJUSTMENT(AdjustmentCode,InventoryID,CreatedByAccountID,SystemQuantity,ActualQuantity,AdjustmentType,Reason,AdjustmentStatus,CreatedAt) VALUES('LEGACY-ADJUSTMENT',{stock.InventoryId},{f.ActorId},100,95,'COUNTING_ERROR',N'Legacy reason','PENDING',SYSUTCDATETIME())");
            var before=await db.Database.SqlQueryRaw<decimal>("SELECT DifferenceQuantity AS Value FROM STOCK_ADJUSTMENT WHERE AdjustmentCode='LEGACY-ADJUSTMENT'").SingleAsync();Assert.Equal(-5,before);
            const string target="20261016163023_AddStockAdjustmentRequestScope";await db.GetService<IMigrator>().MigrateAsync(target);
            var id=await db.StockAdjustments.Where(a=>a.AdjustmentCode=="LEGACY-ADJUSTMENT").Select(a=>a.StockAdjustmentId).SingleAsync();
            var repo=new StockAdjustmentRepository(new DbContextOptionsBuilder<SAW.Infrastructure.Persistence.AppDbContext>().UseSqlServer(f.ConnectionString).Options,new Microsoft.AspNetCore.Http.HttpContextAccessor(),new CloudinaryQrImageStorage(new HttpClient(),new CloudinaryOptions()));
            var detail=await repo.GetAsync(id,default);Assert.Equal("COUNT",detail.Summary.RequestKind);Assert.Equal(-5,detail.Summary.DifferenceQuantity);Assert.Null(Assert.Single(detail.Lines).ReservedQuantity);Assert.Empty(detail.Images);
            await db.GetService<IMigrator>().MigrateAsync(target);Assert.Equal(1,await db.StockAdjustments.CountAsync());Assert.Equal(100,await db.Inventories.Where(i=>i.InventoryId==stock.InventoryId).Select(i=>i.QuantityOnHand).SingleAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261016153240_FixGoodsIssueWorkflowTriggers");
            Assert.Equal(before,await db.Database.SqlQueryRaw<decimal>("SELECT DifferenceQuantity AS Value FROM STOCK_ADJUSTMENT WHERE AdjustmentCode='LEGACY-ADJUSTMENT'").SingleAsync());
            await db.GetService<IMigrator>().MigrateAsync(target);Assert.Equal("Legacy reason",(await repo.GetAsync(id,default)).Reason);
        }
        finally{await f.DisposeAsync();}
    }
}
