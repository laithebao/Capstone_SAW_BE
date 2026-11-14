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
using SAW.API.Middleware;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Entities;

namespace SAW.Test.Infrastructure.UserProfiles;

// Exercises real JWT authorization, MVC binding, service validation and the API error envelope.
// Repository responses are isolated fixtures; no application database or configuration is used.
public sealed class UserProfileApiTests : IAsyncLifetime
{
    private const string Key = "SAW-profile-tests-only-signing-key-0123456789-abcdefghijklmnopqrstuvwxyz";
    private readonly Mock<IUserProfileRepository> repository = new();
    private readonly Account account = new()
    {
        AccountId = 7, Username = "profile_test", FullName = "Original", Email = "login@test.invalid", PasswordHash = "private-hash",
        RoleId = 3, AccountStatus = "ACTIVE", Role = new Role { RoleId = 3, RoleCode = "QC_STAFF" }
    };
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        repository.Setup(x => x.GetByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(UserProfilesController).Assembly);
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<IUserProfileService>(new UserProfileService(repository.Object));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
                RoleClaimType = ClaimTypes.Role, ClockSkew = TimeSpan.Zero
            };
        });
        builder.Services.AddAuthorization();
        app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    }

    [Theory]
    [InlineData("ADMINISTRATOR")][InlineData("WAREHOUSE_MANAGER")][InlineData("QC_STAFF")][InlineData("OPERATION_STAFF")][InlineData("DISTRIBUTOR")]
    public async Task EverySupportedRole_ReadsOwnProfile_AndQueryCannotSelectAnotherAccount(string role)
    {
        account.Role.RoleCode = role;
        Authenticate(role);
        using var result = await client.GetAsync("/api/user-profiles/me?accountId=99");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var json = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, json.GetProperty("data").GetProperty("accountId").GetInt32());
        Assert.Equal(role, json.GetProperty("data").GetProperty("roleCode").GetString());
        Assert.DoesNotContain("private-hash", json.ToString());
        repository.Verify(x => x.GetByAccountIdAsync(99, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnonymousSupplierAndUnknownRoles_CannotReadOrWrite()
    {
        foreach (var role in new string?[] { null, "SUPPLIER", "UNKNOWN" })
        {
            if (role is null) client.DefaultRequestHeaders.Authorization = null;
            else Authenticate(role);
            var expected = role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            using var read = await client.GetAsync("/api/user-profiles/me");
            using var write = await client.PutAsJsonAsync("/api/user-profiles/me", new { fullName = "Changed", phoneNumber = "0901234567" });
            Assert.Equal(expected, read.StatusCode);
            Assert.Equal(expected, write.StatusCode);
        }
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OverpostedIdentityRoleStatusAndEmail_CannotChangeAuthenticationData()
    {
        Authenticate("QC_STAFF");
        using var result = await client.PutAsJsonAsync("/api/user-profiles/me", new
        {
            fullName = " Updated ", phoneNumber = "0901234567", accountId = 99, roleId = 1,
            roleCode = "ADMINISTRATOR", accountStatus = "LOCKED", email = "other@test.invalid", passwordHash = "injected"
        });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("Updated", account.FullName);
        Assert.Equal(7, account.AccountId);
        Assert.Equal(3, account.RoleId);
        Assert.Equal("QC_STAFF", account.Role.RoleCode);
        Assert.Equal("ACTIVE", account.AccountStatus);
        Assert.Equal("login@test.invalid", account.Email);
        Assert.Equal("private-hash", account.PasswordHash);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidationAndTaxCodeChange_Return422WithoutSaving()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = new Distributor { DistributorId = 8, AccountId = 7, DistributorCode = "DIS-7", DistributorName = "Original", TaxCode = "OLD" };
        Authenticate("DISTRIBUTOR");
        using var invalid = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest(" ", "invalid"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
        using var conflict = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Updated", null,
            new("Company", "DUPLICATE", null, null, null, null)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal("Original", account.FullName);
        Assert.Equal("OLD", account.Distributor.TaxCode);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributorCanOmitTaxCodeAndOverpostedStatusCannotChangeRegisteredIdentity()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = new Distributor { DistributorId = 8, DistributorCode = "DIS-7", DistributorName = "Original", TaxCode = "REGISTERED", ProfileStatus = "ACTIVE" };
        Authenticate("DISTRIBUTOR");
        using var response = await client.PutAsJsonAsync("/api/user-profiles/me", new { fullName = "Updated", phoneNumber = "090 123 4567",
            distributor = new { distributorName = "Updated company", phoneNumber = "+84 901 234 567", email = " contact@example.com ", profileStatus = "INACTIVE", distributorCode = "injected" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("REGISTERED", account.Distributor.TaxCode); Assert.Equal("DIS-7", account.Distributor.DistributorCode);
        Assert.Equal("ACTIVE", account.Distributor.ProfileStatus); Assert.Equal("+84901234567", account.Distributor.PhoneNumber); Assert.Equal("contact@example.com", account.Distributor.Email);
    }

    [Fact]
    public async Task ResendingRegisteredTaxCodeAllowsContactUpdateButAChangedCodeIsStillRejected()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = new Distributor { DistributorId = 8, DistributorCode = "DIS-7", DistributorName = "Original company", TaxCode = "0319999002" };
        Authenticate("DISTRIBUTOR");
        using var response = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Updated name", "0901000006",
            new("Updated company", "0319999002", "Contact", "0901000006", "contact@example.com", "Address")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("Updated name", account.FullName); Assert.Equal("0319999002", account.Distributor.TaxCode);
        using var forged = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Forged name", null,
            new("Forged company", "9999999999", null, null, null, null)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
        Assert.Equal("Updated name", account.FullName); Assert.Equal("Updated company", account.Distributor.DistributorName); Assert.Equal("0319999002", account.Distributor.TaxCode);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("1234567890", "valid@example.com")]
    [InlineData("0901234567", "Name <user@example.com>")]
    [InlineData("0901234567", "user@host")]
    public async Task DirectApiCannotBypassPhoneOrEmailFormatChecks(string phone, string email)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = new Distributor { DistributorId = 8, DistributorCode = "DIS-7", DistributorName = "Original", TaxCode = "REGISTERED" };
        Authenticate("DISTRIBUTOR");
        using var response = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Updated", null, new("Updated company", null, null, phone, email, null)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.Equal("Original", account.FullName); Assert.Equal("Original", account.Distributor.DistributorName);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("0", HttpStatusCode.Unauthorized)][InlineData("bad", HttpStatusCode.Unauthorized)][InlineData("99", HttpStatusCode.NotFound)]
    public async Task InvalidOrMissingAccount_Returns401Or404(string id, HttpStatusCode expected)
    {
        Authenticate("QC_STAFF", id);
        using var result = await client.GetAsync("/api/user-profiles/me");
        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task InvalidProfileReturnsEveryFieldMessageSoClientCanShowInlineErrorsWithoutPartialSave()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = new Distributor { DistributorId = 8, DistributorCode = "DIS-7", DistributorName = "Original company", TaxCode = "REGISTERED" };
        Authenticate("DISTRIBUTOR");
        using var response = await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest(" ", "invalid",
            new(" ", null, null, "invalid", "not-email", new string('a', 501))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var messages = body.GetProperty("errors").EnumerateArray().Select(item => item.GetString()!).ToArray();
        foreach (var field in new[] { "Họ và tên", "Số điện thoại cá nhân", "Tên nhà phân phối", "Số điện thoại liên hệ", "Email liên hệ", "Địa chỉ" })
            Assert.Contains(messages, message => message.StartsWith(field, StringComparison.Ordinal));
        Assert.Equal("Original", account.FullName); Assert.Equal("Original company", account.Distributor.DistributorName);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private void Authenticate(string role, string id = "7")
    {
        var token = new JwtSecurityToken(claims: [new Claim("account_id", id), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
    }
}
