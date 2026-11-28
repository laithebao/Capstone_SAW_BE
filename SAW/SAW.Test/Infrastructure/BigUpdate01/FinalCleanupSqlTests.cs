using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.BigUpdate01;
public sealed class FinalCleanupSqlTests(ReceivingSqlFixture f) : IClassFixture<ReceivingSqlFixture>
{
    private AuthenticationHeaderValue Token(string role = "SUPPLIER", int? account = null) => new("Bearer",
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
            [new Claim(JwtRegisteredClaimNames.Sub, (account ?? f.ActorId).ToString()), new Claim("account_id", (account ?? f.ActorId).ToString()), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(15), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body); return json.RootElement.GetProperty("data").Clone();
    }

    [ReceivingSqlFact]
    public async Task CategoryLookupPaginates130CropsAndPreservesSavedLateInactiveAndLegacySelections()
    {
        await using var db = f.Context(); var suffix = Guid.NewGuid().ToString("N")[..8];
        var role = await db.Roles.SingleOrDefaultAsync(x => x.RoleCode == "SUPPLIER")
            ?? new Role { RoleCode = "SUPPLIER", RoleName = "Supplier", IsActive = true };
        (await db.Accounts.FindAsync(f.ActorId))!.Role = role;
        var parent = new Category { Code = "P_" + suffix, Name = "Pagination parent" };
        var child = new Category { Code = "C_" + suffix, Name = "Pagination child", Parent = parent };
        var crops = Enumerable.Range(0, 131).Select(i => new CropType { CropCode = $"C_{suffix}_{i:000}",
            CropName = $"Crop {suffix} {i:000}", CategoryName = parent.Name, Category = child, DefaultUnit = "kg", IsActive = i < 130,
            CreatedAt = DateTime.UtcNow }).ToArray();
        db.CropTypes.AddRange(crops); await db.SaveChangesAsync();
        using var client = await f.ApiAsync(); client.DefaultRequestHeaders.Authorization = Token();
        var ids = new List<int>();
        for (var page = 1; page <= 7; page++)
        {
            var data = await Data(await client.GetAsync($"/api/crop-types/lookup?categoryId={child.CategoryId}&pageSize=20&page={page}"));
            Assert.Equal(130, data.GetProperty("totalCount").GetInt32());
            ids.AddRange(data.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        }
        Assert.Equal(130, ids.Count); Assert.Equal(130, ids.Distinct().Count()); Assert.DoesNotContain(crops[130].CropTypeId, ids);
        var found = await Data(await client.GetAsync($"/api/crop-types/lookup?categoryId={child.CategoryId}&search={crops[125].CropCode}"));
        Assert.Equal(crops[125].CropTypeId, found.GetProperty("items")[0].GetProperty("id").GetInt32());
        var management = await Data(await client.GetAsync("/api/crop-types?pageSize=1000"));
        Assert.Equal(100, management.GetProperty("pageSize").GetInt32()); // management cap stays intact
        if (!await db.SupplierCropTypes.AnyAsync(x => x.SupplierId == f.SupplierId && x.CropTypeId == f.CropTypeId))
        { db.SupplierCropTypes.Add(new SupplierCropType { SupplierId = f.SupplierId, CropTypeId = f.CropTypeId, IsActive = true }); await db.SaveChangesAsync(); }
        var supplier = (await db.Suppliers.FindAsync(f.SupplierId))!;
        var area = await db.GrowingAreas.FindAsync(f.AreaId);
        var request = new UpdateSupplierProfileRequest { SupplierName = supplier.SupplierName, TaxCode = supplier.TaxCode,
            LegalRepresentative = "Test owner", ContactPerson = "Test contact", Address = "Test address", PhoneNumber = "0901234567", Email = "profile@example.invalid",
            CropTypeIds = [f.CropTypeId, crops[125].CropTypeId, crops[129].CropTypeId],
            GrowingAreas = [new SupplierGrowingAreaInputDto { GrowingAreaId = f.AreaId, AreaName = area!.AreaName, AreaInHectares = 1 }] };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/suppliers/me/profile", request)).StatusCode);
        async Task CheckSaved()
        {
            using var profile = JsonDocument.Parse(await client.GetStringAsync("/api/suppliers/me/profile"));
            var selected = profile.RootElement.GetProperty("cropTypes").EnumerateArray().ToArray();
            Assert.Contains(selected, x => x.GetProperty("cropTypeId").GetInt32() == crops[125].CropTypeId && x.GetProperty("cropCode").GetString() == crops[125].CropCode);
            Assert.Equal(3, selected.Length);
        }
        await CheckSaved(); client.DefaultRequestHeaders.Authorization = Token("WAREHOUSE_MANAGER");
        var options = await Data(await client.GetAsync("/api/supply-contracts/options"));
        var owner = options.GetProperty("suppliers").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == f.SupplierId);
        Assert.Contains(crops[129].CropTypeId, owner.GetProperty("cropTypeIds").EnumerateArray().Select(x => x.GetInt32()));
        crops[125].IsActive = false; parent.IsActive = false; await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = Token();
        Assert.Equal(0, (await Data(await client.GetAsync($"/api/crop-types/lookup?categoryId={child.CategoryId}"))).GetProperty("totalCount").GetInt32());
        request.ContactPerson = "Updated contact only";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/suppliers/me/profile", request)).StatusCode);
        await CheckSaved();
    }

    [ReceivingSqlFact]
    public async Task IndependentTabBaselinesNeverAcknowledgeAnotherTabAndLateCommitsAreNotLost()
    {
        using var a = await f.ApiAsync(); using var b = await f.ApiAsync();
        a.DefaultRequestHeaders.Authorization = Token(); b.DefaultRequestHeaders.Authorization = Token();
        await using var db = f.Context();
        async Task<long[]> Baseline(HttpClient c) => (await Data(await c.PostAsync("/api/notifications/popup/initialize", null)))
            .GetProperty("baselineIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        async Task<long[]> Poll(HttpClient c, long[] baseline) => (await Data(await c.PostAsJsonAsync("/api/notifications/popup/poll", new { baselineIds = baseline })))
            .EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToArray();
        var initial = await Baseline(a);
        var fresh = new Notification { AccountId = f.ActorId, NotificationType = "TEST", Title = "New between tabs", Message = "Owned event", CreatedAt = DateTime.UtcNow };
        db.Notifications.Add(fresh); await db.SaveChangesAsync();
        var other = await Baseline(b); Assert.Contains(fresh.NotificationId, other);
        Assert.Empty(await Poll(b, other)); Assert.Contains(fresh.NotificationId, await Poll(a, initial));
        await Baseline(b); await db.Entry(fresh).ReloadAsync(); Assert.Null(fresh.PopupHandledAt); Assert.False(fresh.IsRead);
        await a.PostAsJsonAsync("/api/notifications/popup/acknowledge", new[] { fresh.NotificationId });
        Assert.DoesNotContain(fresh.NotificationId, await Poll(a, initial));
        await db.Entry(fresh).ReloadAsync(); Assert.False(fresh.IsRead);
        // A lower identity is allocated in an uncommitted transaction while another
        // connection commits a later identity. Initializing must neither wait for nor hide it.
        await using var pending = f.Context(retry: false); await using var tx = await pending.Database.BeginTransactionAsync();
        var late = new Notification { AccountId = f.ActorId, NotificationType = "TEST", Title = "Late commit", Message = "Low identity", CreatedAt = DateTime.UtcNow };
        pending.Notifications.Add(late); await pending.SaveChangesAsync();
        var high = new Notification { AccountId = f.ActorId, NotificationType = "TEST", Title = "Committed first", Message = "High identity", CreatedAt = DateTime.UtcNow };
        db.Notifications.Add(high); await db.SaveChangesAsync(); Assert.True(high.NotificationId > late.NotificationId);
        var snapshot = await Baseline(a); Assert.DoesNotContain(late.NotificationId, snapshot); Assert.Contains(high.NotificationId, snapshot);
        await tx.CommitAsync(); Assert.Contains(late.NotificationId, await Poll(a, snapshot));
        b.DefaultRequestHeaders.Authorization = Token("SUPPLIER", int.MaxValue);
        Assert.Empty(await Poll(b, initial));
    }
}
