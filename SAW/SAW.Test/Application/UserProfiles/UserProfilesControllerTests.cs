using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Common;

namespace SAW.Test.Application.UserProfiles;

public sealed class UserProfilesControllerTests
{
    private readonly Mock<IUserProfileService> service = new();
    private UserProfilesController Controller(params Claim[] claims) => new(service.Object)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } }
    };

    [Theory]
    [InlineData("account_id")][InlineData(ClaimTypes.NameIdentifier)]
    public async Task ReadAndUpdate_UseAuthenticatedAccountIdOnly(string claimType)
    {
        var profile = new UserProfileResponse(7, "staff", "staff@saw.local", "Staff", null, null, "QC_STAFF", null);
        using var cancellation = new CancellationTokenSource();
        var request = new UpdateUserProfileRequest("Staff", null);
        service.Setup(x => x.GetAsync(7, cancellation.Token)).ReturnsAsync(profile);
        service.Setup(x => x.UpdateAsync(7, request, cancellation.Token)).ReturnsAsync(profile);
        var controller = Controller(new Claim(claimType, "7"));
        var get = Assert.IsType<OkObjectResult>((await controller.Get(cancellation.Token)).Result);
        Assert.Same(profile, Assert.IsType<ApiResponse<UserProfileResponse>>(get.Value).Data);
        var update = Assert.IsType<OkObjectResult>((await controller.Update(request, cancellation.Token)).Result);
        Assert.Same(profile, Assert.IsType<ApiResponse<UserProfileResponse>>(update.Value).Data);
        service.Verify(x => x.UpdateAsync(7, request, cancellation.Token), Times.Once);
    }

    [Theory]
    [InlineData(null)][InlineData("invalid")][InlineData("0")][InlineData("-1")]
    public async Task MissingOrMalformedIdentity_CannotReadOrWrite(string? value)
    {
        var controller = value is null ? Controller() : Controller(new Claim("account_id", value));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Get(default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Update(new("Staff", null), default));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExplicitAccountClaim_TakesPrecedenceOverNameIdentifier()
    {
        await Controller(new Claim("account_id", "7"), new Claim(ClaimTypes.NameIdentifier, "99")).Get(default);
        service.Verify(x => x.GetAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public void EndpointsRequireSupportedRoles_AndRequestHasNoIdentityOrPermissionFields()
    {
        var authorize = Assert.Single(typeof(UserProfilesController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(new[] { "ADMINISTRATOR", "WAREHOUSE_MANAGER", "QC_STAFF", "OPERATION_STAFF", "DISTRIBUTOR" }, authorize.Roles!.Split(','));
        Assert.Empty(typeof(UserProfilesController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        Assert.Equal(new[] { "FullName", "PhoneNumber", "Distributor" }, typeof(UpdateUserProfileRequest).GetProperties().Select(x => x.Name));
        Assert.Equal(new[] { "DistributorName", "TaxCode", "ContactPerson", "PhoneNumber", "Email", "Address" }, typeof(UpdateDistributorProfileRequest).GetProperties().Select(x => x.Name));
    }
}
