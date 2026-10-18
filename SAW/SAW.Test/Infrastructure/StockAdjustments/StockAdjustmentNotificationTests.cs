using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Repositories;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.StockAdjustments;

public sealed partial class StockAdjustmentSqlTests
{
    [ReceivingSqlFact]
    public async Task Customer_accept_decline_and_withdraw_notify_managers_only_and_replays_do_not_duplicate_delivery()
    {
        foreach (var decision in new[] { "ACCEPT", "DECLINE", "WITHDRAW" })
        {
            var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
            var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
            var sent = await Send(a.Summary.Id, manager); var proposal = sent.Proposals.Single();
            if (decision == "WITHDRAW") {
                await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "ACCEPT", null), sale.Customer, default);
                sent = (await Service().GetAsync(a.Summary.Id, default)).Resolution!;
            }
            await using var db = F.Context();
            var staffCount = await db.Notifications.CountAsync(n => n.AccountId == F.ActorId);
            var managerCount = await db.Notifications.CountAsync(n => n.AccountId == manager && n.NotificationType == "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE");
            var command = new SAW.Application.Features.StockAdjustments.AdjustmentResponseRequest(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, decision, decision == "ACCEPT" ? null : "Phản hồi khách hàng");
            await Resolution().RespondAsync(sale.Order, command, sale.Customer, default);
            await Resolution().RespondAsync(sale.Order, command, sale.Customer, default);
            Assert.Equal(staffCount, await db.Notifications.CountAsync(n => n.AccountId == F.ActorId));
            Assert.Equal(managerCount + 1, await db.Notifications.CountAsync(n => n.AccountId == manager && n.NotificationType == "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE"));
            Assert.False(await db.Notifications.AnyAsync(n => n.AccountId == F.ActorId && n.Title == "Khách đã phản hồi đề xuất"));
        }
    }

    [ReceivingSqlFact]
    public async Task Declined_customer_receives_no_later_count_approval_rejection_or_replacement_notices()
    {
        foreach (var decision in new[] { "APPROVE", "REJECT" })
        {
            var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
            var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
            var sent = await Send(a.Summary.Id, manager); var proposal = sent.Proposals.Single();
            await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "DECLINE", "Không tiếp tục mua"), sale.Customer, default);
            await using var db = F.Context();
            var count = await db.Notifications.CountAsync(n => n.AccountId == sale.Customer);
            Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == sale.Customer && n.Title == "Đơn hàng đã hủy"));
            var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
            await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view, decision, decision == "REJECT" ? "Cần kiểm tra lại" : null), manager, default);
            Assert.Equal(count, await db.Notifications.CountAsync(n => n.AccountId == sale.Customer));
            if (decision == "REJECT") {
                var replacement = await Service().CreateAsync((await Request(s.Batch, actual: 85)) with { ReplacesStockAdjustmentId = a.Summary.Id }, F.ActorId, default);
                view = await Reviews(manager).GetAsync(replacement.Summary.Id, default);
                await Reviews(manager).ReviewAsync(replacement.Summary.Id, Decision(view), manager, default);
                Assert.Equal(count, await db.Notifications.CountAsync(n => n.AccountId == sale.Customer));
            }
            var reviewTitle = decision == "APPROVE" ? "Đã duyệt điều chỉnh tồn" : "Phiếu bị từ chối, lô vẫn tạm dừng";
            Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == F.ActorId && n.RelatedEntityId == a.Summary.Id.ToString() && n.Title == reviewTitle));
        }
    }

    [ReceivingSqlFact]
    public async Task Hidden_legacy_notices_are_excluded_from_paging_unread_counts_and_direct_read_API_without_deleting_evidence()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var sent = await Send(a.Summary.Id, manager); var proposal = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, proposal.Id, proposal.Revision, "DECLINE", "Không mua"), sale.Customer, default);
        await using var db = F.Context();
        var role = await db.Accounts.Where(x => x.AccountId == F.ActorId).Select(x => x.RoleId).SingleAsync();
        var key = Guid.NewGuid().ToString("N");
        var staff = new Account { RoleId = role, Username = key, Email = key + "@test.invalid", FullName = "Notification staff", PasswordHash = "unused", AccountStatus = "ACTIVE" };
        db.Add(staff); await db.SaveChangesAsync();
        Notification Notice(int recipient, string title, string entity, string entityId, string type = "STOCK_ADJUSTMENT") => new() {
            AccountId = recipient, Title = title, Message = "Bằng chứng thông báo", RelatedEntityType = entity, RelatedEntityId = entityId,
            NotificationType = type, Priority = "HIGH", CreatedAt = DateTime.UtcNow };
        var hiddenStaff = Notice(staff.AccountId, "Khách đã phản hồi đề xuất", "STOCK_ADJUSTMENT", a.Summary.Id.ToString());
        var hiddenTyped = Notice(staff.AccountId, "Customer response", "STOCK_ADJUSTMENT", a.Summary.Id.ToString(), "STOCK_ADJUSTMENT_CUSTOMER_RESPONSE");
        var operational = Notice(staff.AccountId, "Đã duyệt điều chỉnh tồn", "STOCK_ADJUSTMENT", a.Summary.Id.ToString());
        var managerResponse = Notice(manager, "Khách đã phản hồi đề xuất", "STOCK_ADJUSTMENT", a.Summary.Id.ToString());
        var hiddenCustomer = Notice(sale.Customer, "Kết quả kiểm tra lô của đơn", "DISTRIBUTOR_ORDER", sale.Order.ToString());
        var hiddenCustomerTyped = Notice(sale.Customer, "Review result", "DISTRIBUTOR_ORDER", sale.Order.ToString(), "STOCK_ADJUSTMENT_REVIEW");
        db.AddRange(hiddenStaff, hiddenTyped, operational, managerResponse, hiddenCustomer, hiddenCustomerTyped); await db.SaveChangesAsync();
        var repository = new NotificationInboxRepository(Options(), Accessor);
        var inbox = await repository.ListAsync(staff.AccountId, 1, 1, default);
        Assert.Equal(1, inbox.TotalCount); Assert.Equal(1, inbox.UnreadCount); Assert.Equal(operational.NotificationId, Assert.Single(inbox.Items).Id);
        Assert.Contains((await repository.ListAsync(manager, 1, 50, default)).Items, n => n.Id == managerResponse.NotificationId);
        var customerInbox = await repository.ListAsync(sale.Customer, 1, 50, default);
        Assert.Equal(2, customerInbox.TotalCount); Assert.Equal(2, customerInbox.UnreadCount); // Original proposal + cancellation.
        Assert.DoesNotContain(customerInbox.Items, n => n.Id == hiddenCustomer.NotificationId || n.Id == hiddenCustomerTyped.NotificationId);
        await Assert.ThrowsAsync<NotFoundException>(() => repository.ReadAsync(sale.Customer, hiddenCustomer.NotificationId, default));
        using var api = await F.ApiAsync();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(staff.AccountId, "OPERATION_STAFF"));
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync($"api/notifications/{hiddenStaff.NotificationId}/read", null)).StatusCode);
        await repository.ReadAsync(staff.AccountId, operational.NotificationId, default);
        Assert.Equal(0, (await repository.ListAsync(staff.AccountId, 1, 1, default)).UnreadCount);
        Assert.False(await db.Notifications.AsNoTracking().Where(n => n.NotificationId == hiddenStaff.NotificationId).Select(n => n.IsRead).SingleAsync());
        Assert.True(await db.Notifications.AnyAsync(n => n.NotificationId == hiddenCustomer.NotificationId));
    }

    [ReceivingSqlFact]
    public async Task Workflow_notifications_use_readable_content_and_old_code_only_messages_are_normalized_without_writes()
    {
        var s = await Scenario(); var sale = await SalesOrder(s.Batch); var manager = await Manager();
        var a = await Service().CreateAsync(await Request(s.Batch, actual: 80), F.ActorId, default);
        var sent = await Send(a.Summary.Id, manager); var p = sent.Proposals.Single();
        await Resolution().RespondAsync(sale.Order, new(Guid.NewGuid(), sent.Version, p.Id, p.Revision, "ACCEPT", null), sale.Customer, default);
        var view = await Reviews(manager).GetAsync(a.Summary.Id, default);
        await Reviews(manager).ReviewAsync(a.Summary.Id, Decision(view), manager, default);
        await using var db = F.Context();
        var messages = await db.Notifications.Where(n => n.AccountId == manager).Select(n => n.Message).ToListAsync();
        Assert.Contains(messages, m => m.Contains("đồng ý"));
        Assert.Contains(messages, m => m.Contains("giá lô"));
        Assert.True(await db.Notifications.AnyAsync(n => n.AccountId == sale.Customer && n.Title == "Đơn đã được điều chỉnh" && n.NotificationType == "STOCK_ADJUSTMENT_REVIEW"));
        Assert.All(messages, m => { Assert.DoesNotContain("SA-", m); Assert.DoesNotContain("ACCEPT", m); Assert.DoesNotContain("DECLINE", m); });

        Notification Legacy(string message) => new() { AccountId = manager, NotificationType = "STOCK_ADJUSTMENT", Priority = "HIGH",
            Title = "Có phiếu điều chỉnh tồn cần xử lý", Message = message, RelatedEntityType = "STOCK_ADJUSTMENT", RelatedEntityId = a.Summary.Id.ToString(), CreatedAt = DateTime.UtcNow };
        var accept = Legacy("ACCEPT · 11"); var decline = Legacy("DECLINE-11");
        var codes = Legacy("SA-" + Guid.NewGuid().ToString("N").ToUpperInvariant() + " · LH-20261003-01");
        var ordinary = Legacy("Hàng đã được kiểm tra lại, vui lòng xem kết quả.");
        db.AddRange(accept, decline, codes, ordinary); await db.SaveChangesAsync();
        var inbox = await new NotificationInboxRepository(Options(), Accessor).ListAsync(manager, 1, 50, default);
        Assert.Contains("đồng ý", inbox.Items.Single(n => n.Id == accept.NotificationId).Message);
        Assert.Contains("không tiếp tục mua", inbox.Items.Single(n => n.Id == decline.NotificationId).Message);
        Assert.DoesNotContain("SA-", inbox.Items.Single(n => n.Id == codes.NotificationId).Message);
        Assert.Equal(ordinary.Message, inbox.Items.Single(n => n.Id == ordinary.NotificationId).Message);
        Assert.Equal("ACCEPT · 11", await db.Notifications.AsNoTracking().Where(n => n.NotificationId == accept.NotificationId).Select(n => n.Message).SingleAsync());
    }
}
