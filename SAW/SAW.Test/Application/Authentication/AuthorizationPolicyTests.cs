using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using SAW.API.Controllers;

namespace SAW.Test.Application.Authentication;

public sealed class AuthorizationPolicyTests
{
    private const string AllAuthenticated = "*";
    private const string Public = "PUBLIC";
    private static readonly string[] Roles = ["ADMINISTRATOR", "WAREHOUSE_MANAGER", "QC_STAFF", "OPERATION_STAFF", "SUPPLIER", "DISTRIBUTOR"];

    // Expected access by feature, independent of the attributes being evaluated.
    private static readonly IReadOnlyDictionary<Type, string> ControllerAccess = new Dictionary<Type, string>
    {
        [typeof(AuthController)] = Public,
        [typeof(AdminDashboardController)] = "ADMINISTRATOR",
        [typeof(AuditLogsController)] = "ADMINISTRATOR",
        [typeof(UserAccessController)] = "ADMINISTRATOR",
        [typeof(CropTypesController)] = "ADMINISTRATOR",
        [typeof(InspectionStandardsController)] = "ADMINISTRATOR,QC_STAFF,WAREHOUSE_MANAGER",
        [typeof(QcInspectionsController)] = "QC_STAFF,ADMINISTRATOR",
        [typeof(EnvironmentLogsController)] = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR",
        [typeof(ProductBatchesController)] = "OPERATION_STAFF,QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR",
        [typeof(ProductBatchQrCodesController)] = "OPERATION_STAFF,QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR",
        [typeof(GoodsReceiptsController)] = "OPERATION_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR",
        [typeof(GoodsIssuesController)] = "OPERATION_STAFF",
        [typeof(PickingOrdersController)] = "OPERATION_STAFF",
        [typeof(StockAdjustmentsController)] = "OPERATION_STAFF",
        [typeof(WarehouseStockAdjustmentsController)] = "WAREHOUSE_MANAGER",
        [typeof(WarehouseInventoryController)] = "WAREHOUSE_MANAGER",
        [typeof(WarehouseOrdersController)] = "WAREHOUSE_MANAGER",
        [typeof(SpoilageForecastController)] = "WAREHOUSE_MANAGER,ADMINISTRATOR",
        [typeof(SuppliersController)] = "SUPPLIER",
        [typeof(SupplierBatchesController)] = "SUPPLIER",
        [typeof(SupplierFilesController)] = "SUPPLIER",
        [typeof(GrowingAreasController)] = AllAuthenticated,
        [typeof(DistributorOrdersController)] = "DISTRIBUTOR",
        [typeof(OrderReturnsController)] = "DISTRIBUTOR",
        [typeof(OrderDeliveryRefusalsController)] = "DISTRIBUTOR,WAREHOUSE_MANAGER",
        [typeof(AiChatController)] = "DISTRIBUTOR",
        [typeof(SupplyContractsController)] = "WAREHOUSE_MANAGER,SUPPLIER,ADMINISTRATOR",
        [typeof(NotificationsController)] = AllAuthenticated,
        [typeof(UserProfilesController)] = "ADMINISTRATOR,WAREHOUSE_MANAGER,QC_STAFF,OPERATION_STAFF,DISTRIBUTOR",
        [typeof(PublicCatalogController)] = Public,
        [typeof(PublicTraceabilityController)] = Public
    };

    private static string ExpectedAccess(Type controller, string action)
    {
        if (controller == typeof(AuthController) && action is "Logout" or "ChangePassword") return AllAuthenticated;
        if (controller == typeof(SupplierFilesController) && action == "Avatar") return Public;
        if (controller == typeof(CropTypesController) && action is "Search" or "Get") return "ADMINISTRATOR,SUPPLIER";
        if (controller == typeof(InspectionStandardsController) && action is "List" or "Create" or "CreateVersion") return "ADMINISTRATOR";
        if (controller == typeof(QcInspectionsController))
        {
            if (action == "RejectBatch") return "ADMINISTRATOR";
            if (action is "List" or "GetById") return "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR";
        }
        if (controller == typeof(ProductBatchesController) && action is "Verify" or "Reject" or "UpdateReceivingDetails") return "OPERATION_STAFF";
        if (controller == typeof(GoodsReceiptsController) && action is "Create" or "UpdateDraft" or "Confirm") return "OPERATION_STAFF";
        if (controller == typeof(OrderDeliveryRefusalsController) && action == "Refuse") return "DISTRIBUTOR";
        if (controller == typeof(SupplyContractsController))
        {
            if (action is "Options" or "Create" or "Update" or "UploadAttachment" or "WarehouseCancel" or "WarehouseCancelReject") return "WAREHOUSE_MANAGER";
            if (action is "EligibleForBatch" or "SupplierCancelRequest" or "SupplierConfirm" or "SupplierReject") return "SUPPLIER";
            if (action is "AdminCancel" or "AdminApprove" or "AdminReject") return "ADMINISTRATOR";
        }
        return ControllerAccess[controller];
    }

    public static IEnumerable<object[]> EndpointCases() => ControllerAccess.Keys.OrderBy(type => type.Name)
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any())
            .Select(method => new object[] { type.Name, method.Name, ExpectedAccess(type, method.Name) }));

    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task EndpointPolicy_GrantsOnlyItsIntendedRoles_AndKeepsAnonymousAccessExplicit(string controllerName, string action, string expected)
    {
        var controller = ControllerAccess.Keys.Single(type => type.Name == controllerName);
        var method = controller.GetMethod(action)!;
        var metadata = controller.GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)).ToArray();
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        Assert.Equal(expected == Public, anonymous);
        using var provider = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
        var policy = await AuthorizationPolicy.CombineAsync(provider.GetRequiredService<IAuthorizationPolicyProvider>(), metadata.OfType<IAuthorizeData>());
        if (expected != Public) Assert.NotNull(policy);
        foreach (var role in Roles.Append("UNKNOWN").Append(string.Empty))
        {
            var principal = role.Length == 0 ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, role)], "Test"));
            var actual = anonymous || (policy is not null && (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, method, policy)).Succeeded);
            var allowed = expected == Public || role.Length > 0 && (expected == AllAuthenticated || expected.Split(',').Contains(role));
            Assert.True(actual == allowed, $"{controllerName}.{action}: role '{role}' expected access={allowed}, actual={actual}.");
        }
    }

    [Fact]
    public void AccessMatrixIncludesEveryBusinessController()
    {
        var controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type) && type != typeof(WeatherForecastController));
        Assert.Equal(controllers.Select(type => type.Name).Order(), ControllerAccess.Keys.Select(type => type.Name).Order());
    }
}
