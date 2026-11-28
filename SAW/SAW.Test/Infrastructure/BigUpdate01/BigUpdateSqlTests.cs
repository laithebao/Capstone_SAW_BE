using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Repositories;
using SAW.Infrastructure.Notifications;
using SAW.Application.Features.SupplyContracts;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories;
using SAW.Infrastructure.Repositories.Suppliers;
using SAW.Test.Infrastructure.ProductBatches;
namespace SAW.Test.Infrastructure.BigUpdate01;

public sealed class BigUpdateSqlTests(ReceivingSqlFixture f) : IClassFixture<ReceivingSqlFixture>
{
    private AuthenticationHeaderValue Token(string role, int? actor = null) => new("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test", "uc28-test", [new Claim("account_id", (actor ?? f.ActorId).ToString()), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(20),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));

    [ReceivingSqlFact]
    public async Task CategoryAuthorizationHierarchyCacheAndConcurrentUniqueness()
    {
        using var client = await f.ApiAsync(); client.DefaultRequestHeaders.Authorization = Token("ADMINISTRATOR");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        async Task<int> Add(string code, int? parent)
        {
            using var response = await client.PostAsJsonAsync("/api/categories", new { name = code, code, parentId = parent, isActive = true, sortOrder = 0 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("data").GetProperty("categoryId").GetInt32();
        }
        var root = await Add("P" + suffix, null); var child = await Add("C" + suffix, root);
        var cropRequest = new { code = "CROP_" + suffix.ToUpperInvariant(), name = "Crop " + suffix, categoryName = "Ignored client category", categoryId = child, defaultUnit = "kg", isActive = true };
        var cropPosts = await Task.WhenAll(client.PostAsJsonAsync("/api/crop-types", cropRequest), client.PostAsJsonAsync("/api/crop-types", cropRequest));
        Assert.Single(cropPosts, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(cropPosts, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/categories", new { name = "Level 3", code = "L" + suffix, parentId = child })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/categories/{root}")).StatusCode);
        var duplicate = new { name = "Concurrent", code = "D" + suffix, parentId = (int?)null };
        var concurrent = await Task.WhenAll(client.PostAsJsonAsync("/api/categories", duplicate), client.PostAsJsonAsync("/api/categories", duplicate));
        Assert.Single(concurrent, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(concurrent, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var db = f.Context(); var crop = await db.CropTypes.FindAsync(f.CropTypeId); crop!.CategoryId = child; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/categories/{root}", new { name = "Renamed parent", code = "P" + suffix, parentId = (int?)null, isActive = true })).StatusCode);
        Assert.Equal("Renamed parent", await db.CropTypes.AsNoTracking().Where(x => x.CropTypeId == f.CropTypeId).Select(x => x.CategoryName).SingleAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/categories/{child}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = Token("SUPPLIER");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/categories/lookup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/categories", duplicate)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/inspection-standards/template")).StatusCode);
        client.DefaultRequestHeaders.Authorization = Token("ADMINISTRATOR");
        var beforeImport = await db.InspectionStandardSets.CountAsync();
        var bytes = await client.GetByteArrayAsync("/api/inspection-standards/template");
        Assert.Empty(SAW.Infrastructure.Inspection.InspectionWorkbook.Parse(bytes).Errors);
        using var upload = new MultipartFormDataContent(); upload.Add(new ByteArrayContent(bytes), "file", "template.xlsx");
        using var preview = await client.PostAsync("/api/inspection-standards/import-preview", upload);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var parsed = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Empty(parsed.RootElement.GetProperty("data").GetProperty("errors").EnumerateArray());
        Assert.Equal(1, parsed.RootElement.GetProperty("data").GetProperty("request").GetProperty("versionNo").GetInt32());
        Assert.Equal(beforeImport, await db.InspectionStandardSets.CountAsync());
    }

    [ReceivingSqlFact]
    public async Task ContractRevisionsAreAtomicImmutableAndSharedAcrossRoles()
    {
        await using var db = f.Context();
        if (!await db.SupplierCropTypes.AnyAsync(x => x.SupplierId == f.SupplierId && x.CropTypeId == f.CropTypeId))
        { db.SupplierCropTypes.Add(new SupplierCropType { SupplierId = f.SupplierId, CropTypeId = f.CropTypeId, IsActive = true }); await db.SaveChangesAsync(); }
        var repository = new SupplyContractRepository(db); var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var created = await repository.CreateAsync(f.ActorId, new(f.SupplierId, f.CropTypeId, 100, "WEEK", 1000.10m, today, today.AddDays(14), "Original terms"), default);
        Assert.NotNull(created.DocumentContent); Assert.Contains("Original terms", created.DocumentContent);
        var crop = await db.CropTypes.FindAsync(f.CropTypeId); var oldName = crop!.CropName; crop.CropName = "Changed " + Guid.NewGuid().ToString("N")[..8]; await db.SaveChangesAsync();
        var supplierActor = await db.Suppliers.Where(x => x.SupplierId == f.SupplierId).Select(x => x.AccountId).SingleAsync();
        var supplier = await repository.GetAsync(supplierActor, "SUPPLIER", created.Id, default);
        var admin = await repository.GetAsync(f.ActorId, "ADMINISTRATOR", created.Id, default);
        Assert.Equal(created.DocumentContent, supplier.DocumentContent); Assert.Equal(created.DocumentContent, admin.DocumentContent);
        Assert.Contains(oldName, supplier.DocumentContent!);
        var updated = await repository.UpdateByWarehouseAsync(f.ActorId, created.Id, new(100, "WEEK", 2000.20m, today, today.AddDays(14), "Revision two"), default);
        Assert.Equal(2, updated.Revision); Assert.Contains("Revision two", updated.DocumentContent!);
        Assert.Equal(created.DocumentContent, await db.SupplyContractRevisions.Where(x => x.SupplyContractId == created.Id && x.Revision == 1).Select(x => x.Content).SingleAsync());
        Assert.Equal(2, await db.SupplyContractRevisions.CountAsync(x => x.SupplyContractId == created.Id));
    }

    [ReceivingSqlFact]
    public async Task PopupBackfillHandlesMoreThanTwentyAndLowerPendingIdsWithoutMarkingRead()
    {
        using var client = await f.ApiAsync(); client.DefaultRequestHeaders.Authorization = Token("SUPPLIER");
        await using var db = f.Context();
        db.Notifications.Add(new Notification { AccountId = f.ActorId, NotificationType = "TEST", Title = "History", Message = "Old", CreatedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
        using var baselineResponse = await client.PostAsync("/api/notifications/popup/initialize", null);
        Assert.Equal(HttpStatusCode.OK, baselineResponse.StatusCode);
        using var baselineJson = JsonDocument.Parse(await baselineResponse.Content.ReadAsStringAsync());
        var baseline = baselineJson.RootElement.GetProperty("data").GetProperty("baselineIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        for (var i = 0; i < 25; i++) db.Notifications.Add(new Notification { AccountId = f.ActorId, NotificationType = "TEST", Title = "New " + i, Message = "New", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        async Task<long[]> Pending()
        {
            using var response = await client.PostAsJsonAsync("/api/notifications/popup/poll", new { baselineIds = baseline });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToArray();
        }
        var first = await Pending(); Assert.Equal(20, first.Length);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/notifications/popup/acknowledge", first)).StatusCode);
        var second = await Pending(); Assert.Equal(5, second.Length); Assert.Empty(first.Intersect(second));
        Assert.False(await db.Notifications.AnyAsync(x => first.Contains(x.NotificationId) && x.IsRead));
        client.DefaultRequestHeaders.Authorization = Token("SUPPLIER", int.MaxValue);
        Assert.Empty(await Pending());
        // A pending lower identity is still read; no MAX(identity) cursor is used.
        client.DefaultRequestHeaders.Authorization = Token("SUPPLIER");
        await db.Notifications.Where(x => x.NotificationId == first[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.PopupHandledAt, (DateTime?)null));
        Assert.Contains(first[0], await Pending());
    }

    [ReceivingSqlFact]
    public async Task HandEnteredAreasKeepHistoryAndCannotBeUsedOrReadByAnotherSupplier()
    {
        await using var db = f.Context();
        if (!await db.SupplierCropTypes.AnyAsync(x => x.SupplierId == f.SupplierId && x.CropTypeId == f.CropTypeId))
        { db.SupplierCropTypes.Add(new SupplierCropType { SupplierId = f.SupplierId, CropTypeId = f.CropTypeId, IsActive = true }); await db.SaveChangesAsync(); }
        var repository = new SupplierRepository(db); var owner = (await db.Suppliers.FindAsync(f.SupplierId))!;
        await repository.UpdateSupplierAsync(owner, [f.CropTypeId], [], [new SupplierGrowingAreaInputDto { AreaName = "Vùng nhập tay", AreaInHectares = 12 }], "{}", null, null);
        var first = await db.GrowingAreas.SingleAsync(x => x.OwnerSupplierId == f.SupplierId && x.AreaName == "Vùng nhập tay");
        Assert.Null(first.Province); Assert.Null(first.Region);
        var batch = await f.BatchAsync(); await db.ProductBatches.Where(x => x.ProductBatchId == batch.ProductBatchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.GrowingAreaId, first.GrowingAreaId));
        await repository.UpdateSupplierAsync(owner, [f.CropTypeId], [], [new SupplierGrowingAreaInputDto { GrowingAreaId = first.GrowingAreaId, AreaName = "Vùng nhập tay sửa", AreaInHectares = 15 }], "{}", null, null);
        Assert.Equal("Vùng nhập tay", await db.GrowingAreas.AsNoTracking().Where(x => x.GrowingAreaId == first.GrowingAreaId).Select(x => x.AreaName).SingleAsync());
        Assert.Equal(first.GrowingAreaId, await db.ProductBatches.Where(x => x.ProductBatchId == batch.ProductBatchId).Select(x => x.GrowingAreaId).SingleAsync());
        Assert.NotEqual(first.GrowingAreaId, (await repository.GetProfileByAccountIdAsync(f.ActorId))!.GrowingAreas.Single().GrowingAreaId);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var actor = await db.Accounts.FindAsync(f.ActorId);
        var other = new Supplier { Account = new Account { RoleId = actor!.RoleId, Username = suffix, Email = suffix + "@example.invalid", FullName = "Other", PasswordHash = "test", AccountStatus = "ACTIVE" }, SupplierCode = suffix, SupplierName = "Other", TaxCode = suffix, ContactPerson = "Other", Address = "Test" };
        db.Accounts.Add(other.Account); await db.SaveChangesAsync(); other.AccountId = other.Account.AccountId;
        var selectableCrop = (await db.CropTypes.FindAsync(f.CropTypeId))!;
        if (selectableCrop.CategoryId is null)
        {
            var root = new Category { Code = "P" + suffix, Name = "Active root" };
            var child = new Category { Code = "C" + suffix, Name = "Active child", Parent = root };
            db.Categories.Add(child); selectableCrop.Category = child; await db.SaveChangesAsync();
        }
        await repository.AddSupplierAsync(other, [f.CropTypeId], [], [new SupplierGrowingAreaInputDto { AreaName = "Vùng riêng người khác", AreaInHectares = 3 }], null, null);
        Assert.Contains((await repository.GetProfileByAccountIdAsync(other.AccountId))!.GrowingAreas, x => x.AreaName == "Vùng riêng người khác");
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateSupplierAsync(other, [f.CropTypeId], [], [new SupplierGrowingAreaInputDto { GrowingAreaId = first.GrowingAreaId, AreaName = "Stolen" }], "{}", null, null));
        using var client = await f.ApiAsync(); client.DefaultRequestHeaders.Authorization = Token("SUPPLIER", other.AccountId);
        using var response = JsonDocument.Parse(await client.GetStringAsync("/api/GrowingAreas"));
        Assert.DoesNotContain(response.RootElement.EnumerateArray(), x => x.GetProperty("growingAreaId").GetInt32() == first.GrowingAreaId);
    }

    [ReceivingSqlFact]
    public async Task OutboxRetriesSmtpFailureAndConcurrentWorkersClaimOnlyOneMessage()
    {
        await using var db = f.Context(); var account = (await db.Accounts.FindAsync(f.ActorId))!;
        db.EmailVerificationTokens.Add(new EmailVerificationToken { AccountId = f.ActorId, TokenHash = Guid.NewGuid().ToString("N"), UsedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var item = new EmailOutbox { AccountId = f.ActorId, Recipient = account.Email, EventKey = "TEST:" + Guid.NewGuid(), Subject = "Test", Body = "Fake sender only" };
        db.EmailOutbox.Add(item); await db.SaveChangesAsync();
        var sender = new Mock<IEmailSender>();
        sender.Setup(x => x.SendBusinessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Isolated SMTP fault"));
        using var provider = new ServiceCollection().AddScoped(_ => f.Context()).AddSingleton(sender.Object).BuildServiceProvider();
        var worker = new EmailOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<EmailOutboxWorker>.Instance);
        Assert.True(await worker.SendNextAsync(default));
        var failed = await db.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Null(failed.SentAt); Assert.Equal(1, failed.Attempts); Assert.Equal(nameof(InvalidOperationException), failed.LastError);
        Assert.True(failed.NextAttemptAt > DateTime.UtcNow);
        await db.EmailOutbox.Where(x => x.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        sender.Reset(); sender.Setup(x => x.SendBusinessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var results = await Task.WhenAll(worker.SendNextAsync(default), worker.SendNextAsync(default));
        Assert.Single(results, x => x);
        sender.Verify(x => x.SendBusinessAsync(account.Email, "Test", "Fake sender only", $"saw-outbox-{item.Id}", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(await db.EmailOutbox.Where(x => x.Id == item.Id).Select(x => x.SentAt).SingleAsync());
        Assert.False(await worker.SendNextAsync(default));
    }
}
