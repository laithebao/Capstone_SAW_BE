using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.Suppliers;

public sealed class GoogleSupplierProfileSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    private async Task<int> GoogleSupplierAsync()
    {
        await using var db = fixture.Context();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.RoleCode == "SUPPLIER");
        if (role is null) { role = new Role { RoleCode = "SUPPLIER", RoleName = "Supplier", IsActive = true }; db.Roles.Add(role); }
        var suffix = Guid.NewGuid().ToString("N");
        var account = new Account { Role = role, Username = "google_" + suffix, Email = suffix + "@test.invalid",
            FullName = "Google supplier", AccountStatus = "ACTIVE", PasswordHash = "test-only", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(new Supplier { Account = account, SupplierCode = suffix[..20], SupplierName = account.FullName,
            ContactPerson = account.FullName, TaxCode = "GOOGLE-" + suffix, Address = "", ProfileStatus = "PENDING_REVIEW", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return account.AccountId;
    }

    private static AuthenticationHeaderValue Bearer(int accountId, string role = "SUPPLIER") => new("Bearer",
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test", "uc28-test",
            [new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString()), new Claim("account_id", accountId.ToString()), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));

    private UpdateSupplierProfileRequest Profile() => new()
    {
        SupplierName = "Completed Google supplier", TaxCode = "TEST-" + Guid.NewGuid().ToString("N"),
        Address = "Test address", LegalRepresentative = "Test owner", ContactPerson = "Test contact",
        PhoneNumber = "0901234567", Email = "supplier@test.invalid", CropTypeIds = [fixture.CropTypeId],
        GrowingAreas = [new SupplierGrowingAreaInputDto { GrowingAreaId = fixture.AreaId, AreaInHectares = 1 }]
    };

    [ReceivingSqlFact]
    public async Task VerifiedGoogleSupplier_CompletesExistingProfile_ThenBatchListIsAccessible()
    {
        var accountId = await GoogleSupplierAsync();
        using var client = await fixture.ApiAsync();
        client.DefaultRequestHeaders.Authorization = Bearer(accountId);
        using var before = await client.GetAsync("/api/SupplierBatches");
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        var profile = Profile();
        using var declaration = await client.PostAsJsonAsync("/api/Suppliers/me/declare", profile);
        Assert.Equal(HttpStatusCode.Conflict, declaration.StatusCode); // The Google placeholder already exists.
        using var update = await client.PutAsJsonAsync("/api/Suppliers/me/profile", profile);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("ACTIVE", (await update.Content.ReadFromJsonAsync<SupplierProfileResponse>())!.ProfileStatus);
        using var list = await client.GetAsync("/api/SupplierBatches");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        await using var db = fixture.Context();
        Assert.Equal("ACTIVE", (await db.Suppliers.SingleAsync(s => s.AccountId == accountId)).ProfileStatus);
        Assert.Equal("ACTIVE", (await db.Accounts.FindAsync(accountId))!.AccountStatus);
        client.DefaultRequestHeaders.Authorization = Bearer(accountId, "DISTRIBUTOR");
        using var wrongRole = await client.PutAsJsonAsync("/api/Suppliers/me/profile", profile);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
    }

    [ReceivingSqlFact]
    public async Task InvalidProfile_RollsBackCompletion_AndDoesNotActivateSupplier()
    {
        var accountId = await GoogleSupplierAsync();
        using var client = await fixture.ApiAsync();
        client.DefaultRequestHeaders.Authorization = Bearer(accountId);
        var profile = Profile();
        profile.GrowingAreas = [new SupplierGrowingAreaInputDto { GrowingAreaId = int.MaxValue }];
        using var update = await client.PutAsJsonAsync("/api/Suppliers/me/profile", profile);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        await using var db = fixture.Context();
        var stored = await db.Suppliers.SingleAsync(s => s.AccountId == accountId);
        Assert.Equal("PENDING_REVIEW", stored.ProfileStatus);
        Assert.StartsWith("GOOGLE-", stored.TaxCode);
        Assert.Null(stored.PhoneNumber);
    }
}
