using SAW.Domain.Entities;
using System.Linq.Expressions;

namespace SAW.Infrastructure.Notifications;
public static class VerifiedEmail
{
    public static readonly Expression<Func<Account, bool>> Eligible = a => a.AccountStatus == "ACTIVE"
        && (a.EmailVerificationTokens.Any(t => t.UsedAt != null)
            || a.AuditLogs.Any(x => x.ActionType == "EMAIL_VERIFIED_GOOGLE" && x.EntityName == "ACCOUNT" && x.Description == a.Email));
}
