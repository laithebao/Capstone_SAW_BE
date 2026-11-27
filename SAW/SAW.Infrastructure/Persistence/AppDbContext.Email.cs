using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
namespace SAW.Infrastructure.Persistence;
public partial class AppDbContext
{
    internal async Task QueueBusinessEmailAsync(int accountId, string key, string subject, string body, CancellationToken ct)
    {
        var recipient = await Accounts.Where(SAW.Infrastructure.Notifications.VerifiedEmail.Eligible)
            .Where(a => a.AccountId == accountId).Select(a => a.Email).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(recipient)) return;
        if (EmailOutbox.Local.Any(x => x.EventKey == key && x.AccountId == accountId)
            || await EmailOutbox.AnyAsync(x => x.EventKey == key && x.AccountId == accountId, ct)) return;
        EmailOutbox.Add(new EmailOutbox { AccountId = accountId, Recipient = recipient, EventKey = key, Subject = subject, Body = body });
    }
}
