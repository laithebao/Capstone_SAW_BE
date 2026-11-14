using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Features.DistributorOrders;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiChat;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class AiToolAuthorizationSqlTests(ReceivingSqlFixture fixture) : IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task BatchTools_AllowVisibleAndOwnedLots_ButIgnoreAccountIdSuppliedByModel()
    {
        var visible = await fixture.BatchAsync();
        var privateLot = await fixture.BatchAsync();
        await using var db = fixture.Context();
        db.Attach(visible);
        visible.BatchStatus = "IN_STOCK";
        foreach (var batch in new[] { visible, privateLot })
        {
            var inspection = fixture.Inspection(batch.ProductBatchId, "COMPLETED");
            inspection.CompletedAt = inspection.StartedAt.AddMinutes(1);
            inspection.QcResult = "PASS";
            db.QcInspections.Add(inspection);
        }

        var role = new Role { RoleCode = "DISTRIBUTOR", RoleName = "Distributor", IsActive = true };
        var owner = Buyer(role, "owner");
        var otherBuyer = Buyer(role, "other");
        var order = new PurchaseOrder
        {
            OrderCode = "CHAT-ORDER",
            DeliveryAddress = "Test address"
        };
        owner.PurchaseOrders.Add(order);
        db.Distributors.AddRange(owner, otherBuyer);
        await db.SaveChangesAsync();
        // The fixture creates the totals as regular columns.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ORDER_DETAIL (PurchaseOrderID, CropTypeID, RequestedProductBatchID,
                RequestedQuantity, RequestedWeightKg, Unit, LineSubtotal, LineTotal)
            VALUES ({order.PurchaseOrderId}, {fixture.CropTypeId}, {privateLot.ProductBatchId},
                1, 100, 'lot', 0, 0)
            """);

        var tools = new AiToolExecutor(db, NullLogger<AiToolExecutor>.Instance, null!,
            new Mock<IDistributorOrderService>(MockBehavior.Strict).Object);
        foreach (var tool in new[] { "GetBatchSummary", "GetBatchQcReport" })
        {
            var publicResult = await ExecuteAsync(tool, visible.BatchCode, otherBuyer.AccountId);
            publicResult["found"]!.GetValue<bool>().Should().BeTrue(tool);
            publicResult["data"]!["batchCode"]!.GetValue<string>().Should().Be(visible.BatchCode);

            var privateId = privateLot.ProductBatchId.ToString(CultureInfo.InvariantCulture);
            var ownerResult = await ExecuteAsync(tool, privateId, owner.AccountId);
            ownerResult["found"]!.GetValue<bool>().Should().BeTrue(tool);
            ownerResult["data"]!["batchCode"]!.GetValue<string>().Should().Be(privateLot.BatchCode);

            var denied = await ExecuteAsync(tool, privateId, otherBuyer.AccountId);
            denied["found"]!.GetValue<bool>().Should().BeFalse(tool);
            denied.AsObject().ContainsKey("data").Should().BeFalse();
        }

        async Task<JsonNode> ExecuteAsync(string tool, string batchCode, int accountId)
        {
            var args = new JsonObject { ["batchCode"] = batchCode, ["accountId"] = owner.AccountId };
            return JsonNode.Parse(await tools.ExecuteAsync(tool, args, accountId, default))!;
        }
    }

    private static Distributor Buyer(Role role, string name) => new()
    {
        DistributorCode = name,
        DistributorName = name,
        TaxCode = name,
        Account = new Account
        {
            Role = role, Username = name, FullName = name, Email = $"{name}@test.invalid",
            PasswordHash = "unused-test-password", AccountStatus = "ACTIVE"
        }
    };
}
