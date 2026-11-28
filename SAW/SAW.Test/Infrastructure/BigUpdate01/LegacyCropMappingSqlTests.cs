using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SAW.Domain.Entities;
using SAW.Infrastructure.Migrations;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.BigUpdate01;

// Exercises the actual versioned SQL, not a copy of its mapping logic. Full schema
// Up/Down is verified separately from the real pre-update backup. Every case here
// rolls back its own disposable SQL fixture; no migration history is invented.
public sealed class LegacyCropMappingSqlTests(ReceivingSqlFixture f) : IClassFixture<ReceivingSqlFixture>
{
    private static readonly string[] OldCodes = ["RICE-ST25", "POTATO-DL", "CARROT-DL", "DEMO-DIST-RICE", "DEMO-DIST-MANGO", "DEMO-DIST-DRAGON"];

    [ReceivingSqlFact]
    public async Task FullPartialAbsentAndExtraLegacyDataKeepIdentityDatesAndUnmappedCrops()
    {
        foreach (var count in new[] { 6, 2, 0 })
        {
            await using var db = f.Context(retry: false); await using var tx = await db.Database.BeginTransactionAsync();
            var existing = await db.CropTypes.CountAsync(); var suffix = Guid.NewGuid().ToString("N")[..8];
            var date = new DateTime(2025, 5, 10, 18, 0, 0, DateTimeKind.Utc);
            var crops = OldCodes.Take(count).Select((code, i) => new CropType { CropCode = code, CropName = "Legacy " + suffix + i,
                CategoryName = "Legacy group", DefaultUnit = "kg", CreatedAt = date }).ToArray();
            var extra = new CropType { CropCode = "TEAM_" + suffix, CropName = "Member crop " + suffix, CategoryName = "Member group", DefaultUnit = "kg", CreatedAt = date.AddDays(-3) };
            db.CropTypes.AddRange(crops); db.CropTypes.Add(extra); await db.SaveChangesAsync();
            var audits = await db.AuditLogs.CountAsync(); var notifications = await db.Notifications.CountAsync();
            var ids = crops.Select(c => c.CropTypeId).ToArray();
            await db.Database.ExecuteSqlRawAsync(((SqlOperation)new NormalizeLegacyCropCodes().UpOperations.Single()).Sql);
            Assert.Equal(existing + count + 1, await db.CropTypes.CountAsync());
            var mapped = await db.CropTypes.AsNoTracking().Where(x => ids.Contains(x.CropTypeId)).ToListAsync();
            Assert.All(mapped, x => { Assert.Equal(date, x.CreatedAt); Assert.NotNull(x.CategoryId); Assert.DoesNotContain(x.CropCode, OldCodes); });
            foreach (var crop in mapped) Assert.EndsWith(Array.IndexOf(ids, crop.CropTypeId) >= 3 ? "11052025" : "10052025", crop.CropCode);
            var kept = await db.CropTypes.AsNoTracking().SingleAsync(x => x.CropTypeId == extra.CropTypeId);
            Assert.Equal(extra.CropCode, kept.CropCode); Assert.Equal(extra.CreatedAt, kept.CreatedAt); Assert.Null(kept.CategoryId);
            Assert.Equal(audits, await db.AuditLogs.CountAsync()); Assert.Equal(notifications, await db.Notifications.CountAsync());
            Assert.Empty(await db.EmailOutbox.ToListAsync()); Assert.Empty(await db.SupplyContractRevisions.ToListAsync());
            await db.Database.ExecuteSqlRawAsync(((SqlOperation)new NormalizeLegacyCropCodes().DownOperations.Single()).Sql);
            var restored = await db.CropTypes.AsNoTracking().Where(x => ids.Contains(x.CropTypeId)).OrderBy(x => x.CropTypeId).Select(x => x.CropCode).ToArrayAsync();
            Assert.Equal(OldCodes.Take(count), restored);
            await tx.RollbackAsync();
        }
    }

    [ReceivingSqlFact]
    public async Task CategoryAndTargetCodeCollisionsFailClearlyWithoutOverwritingOwnedData()
    {
        foreach (var conflict in new[] { "root", "child", "crop" })
        {
            await using var db = f.Context(retry: false); var before = await db.CropTypes.CountAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            var suffix = Guid.NewGuid().ToString("N")[..8];
            if (conflict == "root") db.Categories.Add(new Category { Code = "NC", Name = "Member-owned category" });
            if (conflict == "child") db.Categories.Add(new Category { Code = "GAO", Name = "Member-owned child",
                Parent = new Category { Code = "NC", Name = "Ngũ cốc" } });
            db.CropTypes.Add(new CropType { CropCode = "RICE-ST25", CropName = "Old " + suffix, CategoryName = "Legacy", DefaultUnit = "kg", CreatedAt = new DateTime(2025, 5, 10) });
            if (conflict == "crop") db.CropTypes.Add(new CropType { CropCode = "NC_GAO_ST25_10052025", CropName = "Other owner " + suffix, CategoryName = "Member group", DefaultUnit = "kg", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(((SqlOperation)new NormalizeLegacyCropCodes().UpOperations.Single()).Sql));
            Assert.Equal(conflict == "root" ? 51100 : conflict == "child" ? 51101 : 51103, error.Number);
            await tx.RollbackAsync(); db.ChangeTracker.Clear(); Assert.Equal(before, await db.CropTypes.CountAsync());
        }
    }
}
