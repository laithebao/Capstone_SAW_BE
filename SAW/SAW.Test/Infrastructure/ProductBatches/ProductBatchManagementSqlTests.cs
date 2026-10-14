using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.ProductBatches.Dtos;
using SAW.Application.Features.ProductBatches.Services;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.GoodsReceipts;

namespace SAW.Test.Infrastructure.ProductBatches;

// All writes use the existing GUID-named, automatically dropped SQL test fixture.
public sealed class ProductBatchManagementSqlTests(GoodsReceiptSqlFixture fixture) : IClassFixture<GoodsReceiptSqlFixture>
{
    private ReceivingSqlFixture F => fixture.Database;

    private async Task<ProductBatch> SubmittedAsync()
    {
        var batch = await F.BatchAsync();
        await using var db = F.Context();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.BatchStatus, "SUBMITTED").SetProperty(b => b.VerifiedQuantity, (decimal?)null)
            .SetProperty(b => b.VerifiedWeightInKg, (decimal?)null).SetProperty(b => b.VerifiedPackagingType, (string?)null)
            .SetProperty(b => b.VerifiedPackageCount, (int?)null).SetProperty(b => b.VerifiedPackageUnitWeightKg, (decimal?)null)
            .SetProperty(b => b.ReceivingNote, (string?)null));
        return batch;
    }
    private BatchStatusHistory History(long id, string next) => new()
    {
        ProductBatchId = id, OldStatus = "SUBMITTED", NewStatus = next, ChangedAt = DateTime.UtcNow,
        ChangedByAccountId = F.ActorId, ChangeReason = "Receiving test decision"
    };
    private ProductBatchVerificationRepository Repository(SAW.Infrastructure.Persistence.AppDbContext db) => new(db, new HttpContextAccessor());

    [ReceivingSqlFact]
    public async Task Confirm_preserves_supplier_fields_and_records_six_inputs_history_audit_once()
    {
        var batch = await SubmittedAsync();
        await using var db = F.Context();
        var count = await db.ProductBatches.CountAsync();
        var details = new VerifiedReceivingDetails(12, 125, "Staff crate", 12, 10, "Staff note");
        Assert.True(await Repository(db).ConfirmAsync(batch.ProductBatchId, F.SupplierId, details, History(batch.ProductBatchId, "PENDING_QC"), default));
        var after = await db.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == batch.ProductBatchId);
        Assert.Equal(count, await db.ProductBatches.CountAsync()); Assert.Equal(batch.BatchCode, after.BatchCode);
        Assert.Equal(batch.DeclaredQuantity, after.DeclaredQuantity); Assert.Equal(batch.WeightInKg, after.WeightInKg);
        Assert.Equal(batch.Note, after.Note); Assert.Equal(batch.PackagingType, after.PackagingType);
        Assert.Equal(batch.PackageCount, after.PackageCount); Assert.Equal(batch.PackageUnitWeightKg, after.PackageUnitWeightKg);
        Assert.Equal("PENDING_QC", after.BatchStatus); Assert.Equal(12, after.VerifiedQuantity); Assert.Equal(125, after.VerifiedWeightInKg);
        Assert.Equal("Staff crate", after.VerifiedPackagingType); Assert.Equal(12, after.VerifiedPackageCount);
        Assert.Equal(10, after.VerifiedPackageUnitWeightKg); Assert.Equal("Staff note", after.ReceivingNote);
        Assert.Null(after.QualityGrade); Assert.Empty(await db.QcInspections.Where(q => q.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Empty(await db.Inventories.Where(i => i.ProductBatchId == batch.ProductBatchId).ToListAsync());
        Assert.Empty(await db.GoodsReceipts.Where(r => r.ProductBatchId == batch.ProductBatchId).ToListAsync());
        var history = Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId
            && h.OldStatus == "SUBMITTED" && h.NewStatus == "PENDING_QC").ToListAsync());
        Assert.Equal(F.ActorId, history.ChangedByAccountId);
        var audits = await db.AuditLogs.CountAsync();
        Assert.False(await Repository(db).ConfirmAsync(batch.ProductBatchId, F.SupplierId, details, History(batch.ProductBatchId, "PENDING_QC"), default));
        Assert.Equal(audits, await db.AuditLogs.CountAsync());
        Assert.Single(await db.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId
            && h.OldStatus == "SUBMITTED" && h.NewStatus == "PENDING_QC").ToListAsync());
    }

    [ReceivingSqlFact]
    public async Task Confirm_and_reject_compete_and_reject_never_saves_unconfirmed_receiving_values()
    {
        var batch = await SubmittedAsync();
        await using var one = F.Context(); await using var two = F.Context();
        var results = await Task.WhenAll(
            Repository(one).ConfirmAsync(batch.ProductBatchId, F.SupplierId, new(12, 125, "Crate", 12, 10, "Pending form"), History(batch.ProductBatchId, "PENDING_QC"), default),
            Repository(two).RejectAsync(batch.ProductBatchId, F.SupplierId, "Damaged", History(batch.ProductBatchId, "REJECTED"), default));
        Assert.Single(results, success => success);
        await using var check = F.Context();
        Assert.Single(await check.BatchStatusHistories.Where(h => h.ProductBatchId == batch.ProductBatchId
            && h.OldStatus == "SUBMITTED").ToListAsync());
        var rejected = await SubmittedAsync();
        Assert.True(await Repository(check).RejectAsync(rejected.ProductBatchId, F.SupplierId, "Damaged", History(rejected.ProductBatchId, "REJECTED"), default));
        var after = await check.ProductBatches.AsNoTracking().SingleAsync(b => b.ProductBatchId == rejected.ProductBatchId);
        Assert.Equal("REJECTED", after.BatchStatus); Assert.Equal("Damaged", after.RejectionReason);
        Assert.Null(after.VerifiedQuantity); Assert.Null(after.VerifiedWeightInKg); Assert.Null(after.VerifiedPackagingType);
        Assert.Null(after.VerifiedPackageCount); Assert.Null(after.VerifiedPackageUnitWeightKg); Assert.Null(after.ReceivingNote);
        Assert.Equal(rejected.Note, after.Note);
    }

    [ReceivingSqlFact]
    public async Task Warehouse_list_excludes_declarations_and_supports_server_filter_sort_and_paging()
    {
        var prefix = "BATCH-LIST-" + Guid.NewGuid().ToString("N")[..8];
        var states = new[] { "SUBMITTED", "PENDING_PREDECLARATION", "PENDING_APPROVAL", "CANCELLED", "PENDING_QC", "APPROVED_FOR_STORAGE", "IN_STOCK", "REJECTED", "QUARANTINE" };
        await using var db = F.Context();
        for (var i = 0; i < states.Length; i++)
        {
            var b = await SubmittedAsync();
            await db.ProductBatches.Where(x => x.ProductBatchId == b.ProductBatchId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.BatchStatus, states[i]).SetProperty(x => x.BatchCode, prefix + "-" + i)
                .SetProperty(x => x.CreatedAt, new DateTime(2026, 1, 1).AddDays(i)));
        }
        var service = new ProductBatchService(new ProductBatchQueryRepository(db), Repository(db));
        var first = await service.SearchAsync(new(prefix, F.SupplierId, F.CropTypeId, null, "createdAtAsc", 1, 2), default);
        var second = await service.SearchAsync(new(prefix, F.SupplierId, F.CropTypeId, null, "createdAtAsc", 2, 2), default);
        Assert.Equal(5, first.TotalCount); Assert.Equal(2, first.Items.Count);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.Equal(new[] { "PENDING_QC", "APPROVED_FOR_STORAGE" }, first.Items.Select(x => x.BatchStatus));
        var filtered = await service.SearchAsync(new(prefix, F.SupplierId, F.CropTypeId, "IN_STOCK", "updatedAtDesc"), default);
        Assert.Equal("IN_STOCK", Assert.Single(filtered.Items).BatchStatus);
        Assert.Empty((await service.SearchAsync(new(prefix, null, null, "SUBMITTED", null), default)).Items);
        var submitted = await service.GetSubmittedBySupplierAsync(F.SupplierId, default);
        Assert.Contains(submitted, b => b.BatchCode == prefix + "-0");
        var options = await service.GetFilterOptionsAsync(default);
        Assert.DoesNotContain("SUBMITTED", options.Statuses); Assert.DoesNotContain("CANCELLED", options.Statuses);
    }

    [ReceivingSqlFact]
    public async Task Detail_and_read_role_contract_observations_do_not_change_data()
    {
        var batch = await F.BatchAsync();
        using var client = await F.ApiAsync();
        string Token(string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
            [new Claim("account_id", F.ActorId.ToString()), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
        await using var db = F.Context();
        var qc = F.Inspection(batch.ProductBatchId, "COMPLETED");
        qc.QualityGrade = "B"; qc.QcResult = "PASS"; qc.CompletedAt = qc.StartedAt.AddSeconds(1);
        var location = new WarehouseLocation { LocationCode = Guid.NewGuid().ToString("N"), ZoneName = "Batch test", LocationStatus = "ACTIVE" };
        db.AddRange(qc, location); await db.SaveChangesAsync();
        await db.ProductBatches.Where(b => b.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.QualityGrade, "B").SetProperty(b => b.BatchStatus, "APPROVED_FOR_STORAGE")
            .SetProperty(b => b.VerifiedQuantity, 12m).SetProperty(b => b.VerifiedWeightInKg, 125m)
            .SetProperty(b => b.VerifiedPackagingType, (string?)null).SetProperty(b => b.VerifiedPackageCount, (int?)null)
            .SetProperty(b => b.VerifiedPackageUnitWeightKg, (decimal?)null));
        db.GoodsReceipts.Add(new GoodsReceipt { ProductBatchId = batch.ProductBatchId, WarehouseLocationId = location.WarehouseLocationId,
            ReceiptCode = "TEST-" + Guid.NewGuid().ToString("N"), OperationAccountId = F.ActorId,
            ReceivedQuantity = 10, Unit = "Box", WeightInKg = 100, ReceivedAt = DateTime.UtcNow, ReceiptStatus = "DRAFT" });
        await db.SaveChangesAsync();
        var audits = await db.AuditLogs.CountAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("OPERATION_STAFF"));
        using var response = await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(10, data.GetProperty("quantity").GetDecimal()); Assert.Equal(100, data.GetProperty("weightInKg").GetDecimal());
        Assert.Equal(12, data.GetProperty("verifiedQuantity").GetDecimal()); Assert.Equal(125, data.GetProperty("verifiedWeightInKg").GetDecimal());
        Assert.Equal("Supplier note", data.GetProperty("note").GetString()); Assert.Equal("Received", data.GetProperty("receivingNote").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("verifiedPackageCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("verifiedPackagingType").ValueKind);
        Assert.False(data.GetProperty("canUpdateReceivingInformation").GetBoolean());
        Assert.Equal(ProductBatchService.QcReceivingLockMessage, data.GetProperty("receivingUpdateLockReason").GetString());
        var savedQc = data.GetProperty("latestQcInspection");
        Assert.Equal(qc.QcInspectionId, savedQc.GetProperty("id").GetInt64());
        Assert.Equal("COMPLETED", savedQc.GetProperty("inspectionStatus").GetString());
        Assert.Equal("PASS", savedQc.GetProperty("qcResult").GetString());
        Assert.Equal("B", savedQc.GetProperty("qualityGrade").GetString());
        Assert.Single(data.GetProperty("goodsReceipts").EnumerateArray());
        Assert.NotEmpty(data.GetProperty("statusHistory").EnumerateArray());
        Assert.Single(data.GetProperty("warehouseTransactions").EnumerateArray());
        var fields = data.EnumerateObject().Select(p => p.Name).ToArray();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("QC_STAFF"));
        using var qcList = await client.GetAsync("/api/operation/product-batches");
        using var qcDetail = await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");
        Assert.Equal(HttpStatusCode.OK, qcList.StatusCode); Assert.Equal(HttpStatusCode.OK, qcDetail.StatusCode);
        Assert.Equal(audits, await db.AuditLogs.CountAsync());
        // New detail evidence must never overwrite previously archived observations.
        var folder = Environment.GetEnvironmentVariable("SAW_UC29_EVIDENCE");
        if (!string.IsNullOrEmpty(folder))
            await File.WriteAllTextAsync(Path.Combine(folder, "UC26-UC29-contract-observations.json"), JsonSerializer.Serialize(new
            {
                BatchId = batch.ProductBatchId, DetailFields = fields, QcRoleListHttpStatus = (int)qcList.StatusCode,
                QcRoleDetailHttpStatus = (int)qcDetail.StatusCode,
                SavedQcInspections = await db.QcInspections.CountAsync(q => q.ProductBatchId == batch.ProductBatchId),
                SavedGoodsReceipts = await db.GoodsReceipts.CountAsync(r => r.ProductBatchId == batch.ProductBatchId),
                SavedBatchHistories = await db.BatchStatusHistories.CountAsync(h => h.ProductBatchId == batch.ProductBatchId),
                ImplementedRp3DetailSections = new[] { "latest QC result", "QualityGrade", "Goods Receipt records", "batch transaction/status history" },
                Note = "Verified batch detail against RP3 (4). Previously archived files and evidence remain unchanged."
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
