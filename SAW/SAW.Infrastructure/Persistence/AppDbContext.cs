using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    private static readonly HashSet<Type> ExcludedEntityTypes =
    [
        typeof(AuditLog), typeof(RefreshToken), typeof(PasswordResetToken),
        typeof(EmailVerificationToken)
    ];

    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PasswordHash", "TokenHash", "ReplacedByTokenHash", "JwtId"
    };

    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(options) => _httpContextAccessor = httpContextAccessor;

    // 01. Auth
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountPermission> AccountPermissions => Set<AccountPermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();

    // 02. Business Partners
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Distributor> Distributors => Set<Distributor>();

    // 03. Crop & Certifications
    public DbSet<CropType> CropTypes => Set<CropType>();
    public DbSet<SupplierCropType> SupplierCropTypes => Set<SupplierCropType>();
    public DbSet<SupplierCertification> SupplierCertifications => Set<SupplierCertification>();

    // 03b. Growing Areas
    public DbSet<GrowingArea> GrowingAreas => Set<GrowingArea>();
    public DbSet<SupplierGrowingArea> SupplierGrowingAreas => Set<SupplierGrowingArea>();

    // 04. Inspection Standards
    public DbSet<InspectionStandardSet> InspectionStandardSets => Set<InspectionStandardSet>();
    public DbSet<InspectionStandardVersion> InspectionStandardVersions => Set<InspectionStandardVersion>();
    public DbSet<StandardVersionReview> StandardVersionReviews => Set<StandardVersionReview>();
    public DbSet<InspectionCriterion> InspectionCriteria => Set<InspectionCriterion>();
    public DbSet<CriterionGradeRule> CriterionGradeRules => Set<CriterionGradeRule>();

    // 05. Product Batch
    public DbSet<ProductBatch> ProductBatches => Set<ProductBatch>();
    public DbSet<BatchStatusHistory> BatchStatusHistories => Set<BatchStatusHistory>();

    // 06. Warehouse
    public DbSet<WarehouseSetting> WarehouseSettings => Set<WarehouseSetting>();
    public DbSet<WarehouseLocation> WarehouseLocations => Set<WarehouseLocation>();

    // 07. QC
    public DbSet<QcInspection> QcInspections => Set<QcInspection>();
    public DbSet<InspectionResultDetail> InspectionResultDetails => Set<InspectionResultDetail>();
    public DbSet<SensoryResult> SensoryResults => Set<SensoryResult>();
    public DbSet<LabResult> LabResults => Set<LabResult>();
    public DbSet<QualityImage> QualityImages => Set<QualityImage>();
    public DbSet<EnvironmentLog> EnvironmentLogs => Set<EnvironmentLog>();

    // 08. Inventory
    public DbSet<Inventory> Inventories => Set<Inventory>();

    // 09. Orders & Picking
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<OrderDetail> OrderDetails => Set<OrderDetail>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();
    public DbSet<PickingHistory> PickingHistories => Set<PickingHistory>();

    // 10. Transactions
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsIssue> GoodsIssues => Set<GoodsIssue>();
    public DbSet<GoodsIssueDetail> GoodsIssueDetails => Set<GoodsIssueDetail>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();

    // 11. Traceability, Notification, Audit, AI
    public DbSet<QrCode> QrCodes => Set<QrCode>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiRiskForecast> AiRiskForecasts => Set<AiRiskForecast>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Auto-discover all IEntityTypeConfiguration<T> in this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Cấu hình Khóa chính kép cho SupplierGrowingArea
        modelBuilder.Entity<SupplierGrowingArea>()
            .HasKey(sga => new { sga.SupplierId, sga.GrowingAreaId });

        // Cấu hình quan hệ Many-to-Many
        modelBuilder.Entity<SupplierGrowingArea>()
            .HasOne(sga => sga.Supplier)
            .WithMany(s => s.SupplierGrowingAreas)
            .HasForeignKey(sga => sga.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierGrowingArea>()
            .HasOne(sga => sga.GrowingArea)
            .WithMany(ga => ga.SupplierGrowingAreas)
            .HasForeignKey(sga => sga.GrowingAreaId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cấu hình quan hệ One-to-Many ProductBatch -> GrowingArea
        modelBuilder.Entity<ProductBatch>()
            .HasOne(pb => pb.GrowingArea)
            .WithMany(ga => ga.ProductBatches)
            .HasForeignKey(pb => pb.GrowingAreaId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AppendAuditLogs();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AppendAuditLogs();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AppendAuditLogs()
    {
        ChangeTracker.DetectChanges();
        var httpContext = _httpContextAccessor?.HttpContext;
        var actorId = ReadActorId(httpContext?.User);
        var ipAddress = Limit(httpContext?.Connection.RemoteIpAddress?.ToString(), 64);
        var userAgent = Limit(httpContext?.Request.Headers.UserAgent.ToString(), 500);
        var now = DateTime.UtcNow;

        var changes = ChangeTracker.Entries()
            .Where(ShouldAudit)
            .Select(entry => CreateAuditLog(entry, actorId, ipAddress, userAgent, now))
            .Where(log => log is not null)
            .Cast<AuditLog>()
            .ToList();

        if (changes.Count > 0) AuditLogs.AddRange(changes);
    }

    private static bool ShouldAudit(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
        !ExcludedEntityTypes.Contains(entry.Entity.GetType());

    private static AuditLog? CreateAuditLog(
        EntityEntry entry, int? actorId, string? ipAddress, string? userAgent, DateTime now)
    {
        var action = entry.State switch
        {
            EntityState.Added => "CREATE",
            EntityState.Modified => ModifiedAction(entry),
            EntityState.Deleted => "DELETE",
            _ => null
        };
        if (action is null) return null;

        var oldData = new Dictionary<string, object?>();
        var newData = new Dictionary<string, object?>();
        foreach (var property in entry.Properties.Where(property =>
                     !property.Metadata.IsShadowProperty() &&
                     !SensitiveProperties.Contains(property.Metadata.Name)))
        {
            if (entry.State == EntityState.Modified && !property.IsModified) continue;
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                oldData[property.Metadata.Name] = Normalize(property.OriginalValue);
            if (entry.State is EntityState.Modified or EntityState.Added)
                newData[property.Metadata.Name] = property.IsTemporary ? null : Normalize(property.CurrentValue);
        }

        if (entry.State == EntityState.Modified && oldData.Count == 0) return null;
        var entityName = Limit(entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name, 100)!;
        var entityId = EntityId(entry);
        var changedFields = entry.State == EntityState.Modified ? string.Join(", ", newData.Keys) : null;
        var effectiveActorId = actorId;
        if (effectiveActorId is null && entry.Entity is Account account &&
            action is "LOGIN_SUCCESS" or "LOGIN_FAILED") effectiveActorId = account.AccountId;
        return new AuditLog
        {
            AccountId = effectiveActorId,
            ActionType = action,
            EntityName = entityName,
            EntityId = Limit(entityId, 100),
            OldDataJson = oldData.Count == 0 ? null : JsonSerializer.Serialize(oldData),
            NewDataJson = newData.Count == 0 ? null : JsonSerializer.Serialize(newData),
            Description = Limit(entry.State == EntityState.Modified
                ? $"Cập nhật {entityName}. Trường thay đổi: {changedFields}."
                : $"{(entry.State == EntityState.Added ? "Tạo mới" : "Xóa")} {entityName}.", 1500),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = now
        };
    }

    private static string? EntityId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null) return null;
        var parts = key.Properties.Select(property =>
        {
            var value = entry.Property(property.Name);
            return value.IsTemporary ? null : $"{property.Name}={value.CurrentValue}";
        }).Where(value => value is not null);
        var result = string.Join(";", parts!);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string ModifiedAction(EntityEntry entry)
    {
        if (entry.Entity is not Account) return "UPDATE";
        bool Changed(string property) => entry.Property(property).IsModified;
        if (Changed(nameof(Account.LastLoginAt))) return "LOGIN_SUCCESS";
        if (Changed(nameof(Account.FailedLoginAttempts)) || Changed(nameof(Account.LockoutEnd))) return "LOGIN_FAILED";
        if (Changed(nameof(Account.PasswordHash))) return "PASSWORD_CHANGED";
        if (Changed(nameof(Account.AccountStatus))) return "ACCOUNT_STATUS_CHANGED";
        if (Changed(nameof(Account.RoleId))) return "ACCOUNT_ROLE_CHANGED";
        return "UPDATE";
    }

    private static int? ReadActorId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirstValue("account_id") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : null;
    }

    private static object? Normalize(object? value) => value switch
    {
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O"),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O"),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value
    };

    private static string? Limit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
}
