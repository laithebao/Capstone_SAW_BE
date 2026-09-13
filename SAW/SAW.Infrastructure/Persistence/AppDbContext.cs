using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

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
    }
}
