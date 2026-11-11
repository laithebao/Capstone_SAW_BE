using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Test.Infrastructure.Suppliers;

public sealed class SupplierProfileApiTests : IAsyncLifetime
{
    private const string Key = "SAW-supplier-profile-test-only-0123456789-abcdefghijklmnopqrstuvwxyz";
    private readonly Mock<ISupplierRepository> repository = new();
    private readonly Supplier supplier = new() { SupplierId = 9, AccountId = 7, SupplierCode = "SUP-7", SupplierName = "Original", TaxCode = "0123456789",
        PhoneNumber = "0901111111", Email = "old@example.com", ProfileStatus = "ACTIVE", Account = new Account { AccountId = 7, Username = "supplier", AccountStatus = "ACTIVE" } };
    private WebApplication app = null!;
    private HttpClient client = null!;
    private static UpdateSupplierProfileRequest Request(string phone = "0901234567", string email = "contact@example.com") => new()
    { SupplierName = "Updated", TaxCode = "0123456789", Address = "Address", LegalRepresentative = "Owner", ContactPerson = "Contact", PhoneNumber = phone, Email = email, CropTypeIds = [1] };

    public async Task InitializeAsync()
    {
        repository.Setup(r => r.GetEntityByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(supplier);
        repository.Setup(r => r.GetProfileByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(() => new SupplierProfileResponse
            { AccountId = 7, SupplierId = 9, TaxCode = supplier.TaxCode, PhoneNumber = supplier.PhoneNumber, Email = supplier.Email, ProfileStatus = supplier.ProfileStatus });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(SuppliersController).Assembly);
        builder.Services.AddSingleton<ISupplierCommandService>(new SupplierCommandService(repository.Object));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = false, ValidateAudience = false,
                ValidateLifetime = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), RoleClaimType = ClaimTypes.Role };
        });
        builder.Services.AddAuthorization(); app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
        var token = new JwtSecurityToken(claims: [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "SUPPLIER")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Fact]
    public async Task ForgedTaxCodeIsRejectedOverHttpBeforeAnySupplierChange()
    {
        var request = Request(); request.TaxCode = "9999999999";
        using var response = await client.PutAsJsonAsync("/api/Suppliers/me/profile", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("Mã số thuế", await response.Content.ReadAsStringAsync());
        Assert.Equal("0123456789", supplier.TaxCode); Assert.Equal("Original", supplier.SupplierName); VerifyNoWrite();
    }

    [Theory]
    [InlineData("1234567890", "contact@example.com")]
    [InlineData("0901234567", "Name <user@example.com>")]
    [InlineData("0901234567", "user@host")]
    public async Task InvalidContactFormatsAreRejectedOnBothCreateAndUpdate(string phone, string email)
    {
        using var update = await client.PutAsJsonAsync("/api/Suppliers/me/profile", Request(phone, email));
        using var create = await client.PostAsJsonAsync("/api/Suppliers/me/declare", Request(phone, email));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal("Original", supplier.SupplierName); VerifyNoWrite();
    }

    [Fact]
    public async Task ValidContactUpdateKeepsRegisteredTaxCodeAndReturnsNormalizedPhone()
    {
        using var response = await client.PutAsJsonAsync("/api/Suppliers/me/profile", Request("+84 901 234 567", "contact@example.com"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var result = (await response.Content.ReadFromJsonAsync<SupplierProfileResponse>())!;
        Assert.Equal("0123456789", result.TaxCode); Assert.Equal("+84901234567", result.PhoneNumber);
    }

    [Fact]
    public async Task GooglePlaceholderCanBeCompletedOnceAndRegisteredTaxCodeThenLocks()
    {
        supplier.ProfileStatus = "PENDING_REVIEW"; supplier.TaxCode = "GOOGLE-placeholder"; supplier.Account.Username = "google_user";
        using var completion = await client.PutAsJsonAsync("/api/Suppliers/me/profile", Request());
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode); Assert.Equal("ACTIVE", supplier.ProfileStatus); Assert.Equal("0123456789", supplier.TaxCode);
        var request = Request(); request.TaxCode = "9999999999";
        using var retry = await client.PutAsJsonAsync("/api/Suppliers/me/profile", request);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode); Assert.Equal("0123456789", supplier.TaxCode);
    }

    private void VerifyNoWrite()
    {
        repository.Verify(r => r.UpdateSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.AddSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData("SupplierName")]
    [InlineData("ContactPerson")]
    [InlineData("LegalRepresentative")]
    [InlineData("Address")]
    public async Task RequiredProfileFieldsCannotBeWhitespaceEvenWhenContactsAreValid(string field)
    {
        var request = Request(); typeof(UpdateSupplierProfileRequest).GetProperty(field)!.SetValue(request, " ");
        using var response = await client.PutAsJsonAsync("/api/Suppliers/me/profile", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("Original", supplier.SupplierName); VerifyNoWrite();
    }

    [Fact]
    public async Task SuppliedProductTypeIsRequiredBeforeProfileUpdate()
    {
        var request = Request(); request.CropTypeIds = [];
        using var response = await client.PutAsJsonAsync("/api/Suppliers/me/profile", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); VerifyNoWrite();
    }

    [Fact]
    public async Task InvalidSupplierReturnsFieldKeysForInlineFeedbackAndDoesNotPersist()
    {
        var request = Request("invalid", "not-email");
        request.SupplierName = " "; request.LegalRepresentative = " "; request.ContactPerson = " "; request.Address = " "; request.CropTypeIds = [];
        using var response = await client.PutAsJsonAsync("/api/Suppliers/me/profile", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        foreach (var field in new[] { "SupplierName", "LegalRepresentative", "ContactPerson", "Address", "CropTypeIds", "PhoneNumber", "Email" })
            Assert.Contains(errors.EnumerateObject(), error => error.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && error.Value.GetArrayLength() > 0);
        Assert.Equal("Original", supplier.SupplierName); VerifyNoWrite();
    }
    public async Task DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
}
