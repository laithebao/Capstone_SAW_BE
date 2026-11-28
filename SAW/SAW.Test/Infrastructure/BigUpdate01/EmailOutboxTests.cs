using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
namespace SAW.Test.Infrastructure.BigUpdate01;
public sealed class EmailOutboxTests
{
    [Theory]
    [InlineData(true, "PENDING_QC", 1)]
    [InlineData(true, "APPROVED_FOR_STORAGE", 1)]
    [InlineData(true, "REJECTED", 1)]
    [InlineData(false, "PENDING_QC", 0)]
    [InlineData(true, "SUBMITTED", 0)]
    public async Task OnlyVerifiedOwnerAndRequestedTransitionsCreateOneDurableRequest(bool verified, string status, int count)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var role = new Role { RoleId = 1, RoleCode = "SUPPLIER", RoleName = "Supplier" };
        var account = new Account { AccountId = 1, Role = role, Username = "test", Email = "owner@example.invalid", FullName = "Owner", PasswordHash = "test", AccountStatus = "ACTIVE" };
        db.Accounts.Add(account);
        if (verified) db.EmailVerificationTokens.Add(new EmailVerificationToken { AccountId = 1, TokenHash = "test", UsedAt = DateTime.UtcNow });
        db.Suppliers.Add(new Supplier { SupplierId = 1, AccountId = 1, SupplierName = "Supplier", SupplierCode = "SUP", TaxCode = "TEST", ContactPerson = "Contact", Address = "Test" });
        var batch = new ProductBatch { ProductBatchId = 1, SupplierId = 1, BatchCode = "BATCH", ProductName = "Produce", Unit = "kg", BatchStatus = "NEW", CreatedAt = DateTime.UtcNow };
        db.ProductBatches.Add(batch); await db.SaveChangesAsync();
        batch.BatchStatus = status; batch.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(); await db.SaveChangesAsync();
        Assert.Equal(count, await db.EmailOutbox.CountAsync());
        if (count == 1) { var email = await db.EmailOutbox.SingleAsync(); Assert.Equal(1, email.AccountId); Assert.Null(email.SentAt); Assert.Equal("owner@example.invalid", email.Recipient); }
    }
}
