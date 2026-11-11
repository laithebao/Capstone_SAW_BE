using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;

namespace SAW.Test.Infrastructure.UserProfiles;

public sealed class UserProfileRepositoryTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static Account Account(int id, string role = "DISTRIBUTOR") => new()
    {
        AccountId = id, Username = $"account{id}", Email = $"account{id}@test.invalid", FullName = $"Account {id}", PasswordHash = "private-password-hash",
        Role = new Role { RoleCode = role, RoleName = role }, AccountStatus = "ACTIVE"
    };

    [Fact]
    public async Task RepositoryLoadsOnlyRequestedAccountAndItsRoleAndDistributor()
    {
        await using var db = Database();
        var own = Account(7); var other = Account(8);
        db.AddRange(own, other, new Distributor { Account = own, DistributorCode = "D-7", DistributorName = "Own company", TaxCode = "TAX-7" },
            new Distributor { Account = other, DistributorCode = "D-8", DistributorName = "Other company", TaxCode = "TAX-8" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var repository = new UserProfileRepository(db); var result = await repository.GetByAccountIdAsync(7, default);
        Assert.Equal("DISTRIBUTOR", result!.Role.RoleCode); Assert.Equal("Own company", result.Distributor!.DistributorName);
        Assert.Null(await repository.GetByAccountIdAsync(999, default));
    }

    [Fact]
    public async Task TaxUniquenessIncludesBothPartnerTablesButExcludesTheCurrentDistributor()
    {
        await using var db = Database(); var own = Account(7); var other = Account(8); var supplier = Account(9, "SUPPLIER");
        var distributor = new Distributor { Account = own, DistributorCode = "D-7", DistributorName = "Own", TaxCode = "TAX-7" };
        db.AddRange(distributor, new Distributor { Account = other, DistributorCode = "D-8", DistributorName = "Other", TaxCode = "TAX-8" },
            new Supplier { Account = supplier, SupplierCode = "S-9", SupplierName = "Supplier", TaxCode = "TAX-9", Address = "Address", ContactPerson = "Contact" });
        await db.SaveChangesAsync(); var repository = new UserProfileRepository(db);
        Assert.False(await repository.TaxCodeExistsAsync("TAX-7", distributor.DistributorId, default));
        Assert.True(await repository.TaxCodeExistsAsync("TAX-8", distributor.DistributorId, default));
        Assert.True(await repository.TaxCodeExistsAsync("TAX-9", distributor.DistributorId, default));
        Assert.False(await repository.TaxCodeExistsAsync("NEW-TAX", distributor.DistributorId, default));
    }

    [Fact]
    public async Task SavingProfilePersistsPersonalAndBusinessFieldsAndProducesAuditsWithoutPasswordHashes()
    {
        await using var db = Database(); var own = Account(7);
        db.Add(new Distributor { Account = own, DistributorCode = "D-7", DistributorName = "Before company", TaxCode = "TAX-7" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var auditBefore = await db.AuditLogs.CountAsync();
        await new UserProfileService(new UserProfileRepository(db)).UpdateAsync(7,
            new("Updated name", "0901234567", new("Updated company", null, "Contact", null, null, "Address")), default);
        db.ChangeTracker.Clear();
        var saved = await db.Accounts.Include(a => a.Distributor).SingleAsync(a => a.AccountId == 7);
        Assert.Equal("Updated name", saved.FullName); Assert.Equal("Updated company", saved.Distributor!.DistributorName);
        Assert.Equal("TAX-7", saved.Distributor.TaxCode); Assert.Equal("private-password-hash", saved.PasswordHash);
        var audits = await db.AuditLogs.AsNoTracking().ToListAsync(); Assert.True(audits.Count > auditBefore);
        Assert.DoesNotContain(audits, log => (log.NewDataJson ?? "").Contains("private-password-hash") || (log.OldDataJson ?? "").Contains("private-password-hash"));
        Assert.Contains(audits, log => log.ActionType == "UPDATE" && (log.NewDataJson ?? "").Contains("Updated name"));
    }
}
