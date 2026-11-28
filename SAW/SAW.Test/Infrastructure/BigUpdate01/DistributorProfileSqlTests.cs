using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Application.Features.UserProfiles;
using SAW.Application.Repositories;
using SAW.Domain.Entities;
using SAW.Infrastructure.Authentication;
using SAW.Infrastructure.Notifications;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.BigUpdate01;
public sealed class DistributorProfileSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public Task NormalRegistrationMustCompleteProfileBeforeBusinessApis() => Flow(false);
    [ReceivingSqlFact]
    public Task GoogleRegistrationMustCompleteProfileAndPersistsTrustedEmailProof() => Flow(true);

    private async Task Flow(bool google)
    {
        await using var db = fixture.Context();
        var role = await db.Roles.SingleOrDefaultAsync(x => x.RoleCode == "DISTRIBUTOR");
        if (role is null) { role = new Role { RoleCode = "DISTRIBUTOR", RoleName = "Distributor", IsActive = true }; db.Roles.Add(role); await db.SaveChangesAsync(); }
        var suffix = Guid.NewGuid().ToString("N");
        var email = suffix + "@example.invalid";
        string? verification = null;
        var sender = new Mock<IEmailSender>();
        sender.Setup(x => x.SendEmailVerificationAsync(email, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, token, _) => verification = token).Returns(Task.CompletedTask);
        var identity = new Mock<IGoogleIdentityService>();
        identity.Setup(x => x.VerifyAsync("isolated-google-token", It.IsAny<CancellationToken>())).ReturnsAsync(new GoogleIdentity(suffix, email, "Google owner"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Jwt:Key"] = ReceivingSqlFixture.JwtKey, ["Jwt:Issuer"] = "uc28-test", ["Jwt:Audience"] = "uc28-test"
        }).Build();
        var auth = new AuthCommandService(new AuthRepository(db), new BcryptPasswordHasher(), new JwtTokenService(config), sender.Object, identity.Object);
        if (google)
            await Assert.ThrowsAsync<ForbiddenException>(() => auth.GoogleLoginAsync(new("isolated-google-token", role.RoleId), null, default));
        else
            await auth.RegisterAsync(new("review_" + suffix, email, "Review-password-2026!", "Normal owner", null,
                role.RoleId, "Test company", "TAX-" + suffix, null), default);
        Assert.NotNull(verification);
        await auth.VerifyEmailAsync(new(verification), default);
        var session = google ? (await auth.GoogleLoginAsync(new("isolated-google-token", null), null, default)).Session!
            : await auth.LoginAsync(new(email, "Review-password-2026!"), null, default);
        using var client = await fixture.ApiAsync(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        async Task<bool> Complete()
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/api/user-profiles/me"));
            return json.RootElement.GetProperty("data").GetProperty("isProfileComplete").GetBoolean();
        }
        Assert.False(await Complete());
        var order = new { requestId = Guid.NewGuid(), lots = Array.Empty<object>(), deliveryAddress = "Test address", contactPhone = "0901234567" };
        var blocked = await client.PostAsJsonAsync("/api/distributor/orders", order);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("DISTRIBUTOR_PROFILE_INCOMPLETE", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/distributor/orders/catalog")).StatusCode);
        var account = await db.Accounts.Include(x => x.Distributor).SingleAsync(x => x.Email == email);
        var business = new UpdateDistributorProfileRequest("Company completed", google ? "TAX-" + suffix : account.Distributor!.TaxCode, null, null, null, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Owner", null, business with { DistributorName = " " }))).StatusCode);
        Assert.False(await Complete());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Owner", null, business))).StatusCode);
        Assert.True(await Complete());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/distributor/orders")).StatusCode);
        using var reopened = await fixture.ApiAsync(); reopened.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.OK, (await reopened.GetAsync("/api/distributor/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync("/api/user-profiles/me", new UpdateUserProfileRequest("Owner", null, business with { TaxCode = "CHANGED" }))).StatusCode);
        if (google)
        {
            Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.AccountId == account.AccountId && x.ActionType == "EMAIL_VERIFIED_GOOGLE"));
            // Only in the disposable fixture: prove Google evidence works without a local token.
            await db.EmailVerificationTokens.Where(x => x.AccountId == account.AccountId).ExecuteDeleteAsync();
            Assert.True(await db.Accounts.Where(VerifiedEmail.Eligible).AnyAsync(x => x.AccountId == account.AccountId));
        }
    }
}
