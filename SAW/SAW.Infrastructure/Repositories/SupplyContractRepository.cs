using System.Data;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Application.Features.SupplyContracts;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class SupplyContractRepository(AppDbContext db) : ISupplyContractRepository
{
    private static readonly string AttachmentRoot = Path.Combine(AppContext.BaseDirectory, "App_Data", "ContractFiles");

    public async Task<SupplyContractDetail> AttachFileAsync(int warehouseManagerAccountId, long id, SupplyContractAttachment attachment, CancellationToken ct)
    {
        var contract = await db.SupplyContracts.SingleOrDefaultAsync(x => x.SupplyContractId == id && x.WarehouseManagerAccountId == warehouseManagerAccountId, ct)
            ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền đính kèm file.");
        Directory.CreateDirectory(AttachmentRoot);
        var storageName = $"{Guid.NewGuid():N}{Path.GetExtension(attachment.FileName).ToLowerInvariant()}";
        await File.WriteAllBytesAsync(Path.Combine(AttachmentRoot, storageName), attachment.Content, ct);
        if (!string.IsNullOrWhiteSpace(contract.AttachmentStorageName))
            File.Delete(Path.Combine(AttachmentRoot, contract.AttachmentStorageName));
        contract.AttachmentFileName = attachment.FileName;
        contract.AttachmentStorageName = storageName;
        contract.AttachmentContentType = attachment.ContentType;
        contract.AttachmentUploadedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", id, ct);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAttachmentAsync(int actorAccountId, string actorRole, long id, CancellationToken ct)
    {
        var contract = await VisibleContracts(actorAccountId, actorRole).AsNoTracking().SingleOrDefaultAsync(x => x.SupplyContractId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền xem file.");
        if (string.IsNullOrWhiteSpace(contract.AttachmentStorageName) || string.IsNullOrWhiteSpace(contract.AttachmentFileName))
            throw new NotFoundException("Hợp đồng chưa có file đính kèm.");
        var path = Path.Combine(AttachmentRoot, contract.AttachmentStorageName);
        if (!File.Exists(path)) throw new NotFoundException("File đính kèm không còn trên hệ thống.");
        return (await File.ReadAllBytesAsync(path, ct), contract.AttachmentContentType ?? "application/octet-stream", contract.AttachmentFileName);
    }

    public async Task<SupplyContractPage> ListAsync(
        int actorAccountId, string actorRole, SupplyContractQuery query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var contracts = VisibleContracts(actorAccountId, actorRole).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var keyword = query.Search.Trim();
            contracts = contracts.Where(x => x.ContractCode.Contains(keyword)
                || x.Supplier.SupplierName.Contains(keyword)
                || x.CropType.CropName.Contains(keyword));
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
            contracts = contracts.Where(x => x.ContractStatus == query.Status);

        var total = await contracts.CountAsync(ct);
        var descending = query.SortDirection != "asc";
        var orderedContracts = query.SortBy switch
        {
            "enddate" => descending
                ? contracts.OrderByDescending(x => x.EndDate)
                : contracts.OrderBy(x => x.EndDate),
            "unitprice" => descending
                ? contracts.OrderByDescending(x => x.UnitPriceVndPerKg)
                : contracts.OrderBy(x => x.UnitPriceVndPerKg),
            "deliveryquantity" => descending
                ? contracts.OrderByDescending(x => x.DeliveryQuantityKg)
                : contracts.OrderBy(x => x.DeliveryQuantityKg),
            _ => descending
                ? contracts.OrderByDescending(x => x.CreatedAt)
                : contracts.OrderBy(x => x.CreatedAt)
        };
        var items = await orderedContracts
            .ThenByDescending(x => x.SupplyContractId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new SupplyContractSummary(
                x.SupplyContractId,
                x.ContractCode,
                x.Supplier.SupplierName,
                x.CropType.CropName,
                x.DeliveryQuantityKg,
                x.DeliveryPeriod,
                x.UnitPriceVndPerKg,
                x.StartDate,
                x.EndDate,
                x.ContractStatus,
                x.Revision,
                x.CreatedAt,
                x.ContractStatus == "APPROVED"
                    && x.StartDate <= today
                    && x.EndDate >= today
                    && !db.SupplyContracts.Any(newer =>
                        newer.SupplierId == x.SupplierId
                        && newer.CropTypeId == x.CropTypeId
                        && newer.ContractStatus == "APPROVED"
                        && newer.StartDate <= today
                        && newer.EndDate >= today
                        && (newer.CreatedAt > x.CreatedAt
                            || (newer.CreatedAt == x.CreatedAt
                                && newer.SupplyContractId > x.SupplyContractId)))))
            .ToListAsync(ct);
        return new SupplyContractPage(items, total, query.Page, query.PageSize);
    }

    public async Task<SupplyContractDetail> GetAsync(
        int actorAccountId, string actorRole, long id, CancellationToken ct)
    {
        var detail = await VisibleContracts(actorAccountId, actorRole)
            .AsNoTracking()
            .Where(x => x.SupplyContractId == id)
            .Select(x => new SupplyContractDetail(
                x.SupplyContractId,
                x.ContractCode,
                x.SupplierId,
                x.Supplier.SupplierName,
                x.CropTypeId,
                x.CropType.CropName,
                x.DeliveryQuantityKg,
                x.DeliveryPeriod,
                x.UnitPriceVndPerKg,
                x.StartDate,
                x.EndDate,
                x.Terms,
                x.ContractStatus,
                x.Revision,
                x.WarehouseManagerAccount.FullName,
                x.CreatedAt,
                x.SupplierRespondedAt,
                x.SupplierRejectionReason,
                x.AdminAccount == null ? null : x.AdminAccount.FullName,
                x.AdminReviewedAt,
                x.AdminRejectionReason,
                x.CancellationRequestedAt,
                x.CancellationRequestReason,
                x.CancelledAt,
                x.CancellationReason,
                x.AttachmentFileName,
                x.AttachmentContentType,
                x.AttachmentUploadedAt,
                false,
                null))
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền xem hợp đồng này.");

        var missedDueDate = await FindLatestMissedDeliveryDueDateAsync(id, detail.Status,
            detail.DeliveryQuantityKg, detail.StartDate, detail.EndDate, detail.DeliveryPeriod, ct);
        return detail with
        {
            HasOverdueDelivery = missedDueDate is not null,
            OverdueDeliveryDueDate = missedDueDate
        };
    }

    public async Task<SupplyContractOptions> GetOptionsAsync(CancellationToken ct)
    {
        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(x => x.ProfileStatus == "ACTIVE" && x.Account.AccountStatus == "ACTIVE")
            .OrderBy(x => x.SupplierName)
            .Select(x => new SupplyContractSupplierOption(
                x.SupplierId, x.SupplierCode, x.SupplierName,
                x.SupplierCropTypes.Where(link => link.IsActive && link.CropType.IsActive)
                    .Select(link => link.CropTypeId).ToList()))
            .ToListAsync(ct);
        var crops = await db.CropTypes.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.CropName)
            .Select(x => new SupplyContractCropTypeOption(
                x.CropTypeId, x.CropCode, x.CropName, x.CategoryName))
            .ToListAsync(ct);
        return new SupplyContractOptions(suppliers, crops);
    }

    public async Task<IReadOnlyList<SupplyContractBatchOption>> GetEligibleForBatchAsync(
        int supplierAccountId, DateOnly expectedDeliveryDate, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var contracts = await db.SupplyContracts.AsNoTracking()
            .Where(x => x.Supplier.AccountId == supplierAccountId
                && x.ContractStatus == "APPROVED"
                && x.StartDate <= today
                && x.EndDate >= today
                && db.SupplierCropTypes.Any(supplierCrop =>
                    supplierCrop.SupplierId == x.SupplierId
                    && supplierCrop.CropTypeId == x.CropTypeId
                    && supplierCrop.IsActive
                    && supplierCrop.CropType.IsActive))
            .OrderBy(x => x.EndDate)
            .ThenBy(x => x.ContractCode)
            .Select(x => new
            {
                x.SupplyContractId,
                x.ContractCode,
                x.CropTypeId,
                CropTypeName = x.CropType.CropName,
                x.CropType.CategoryName,
                x.DeliveryQuantityKg,
                x.DeliveryPeriod,
                x.UnitPriceVndPerKg,
                x.StartDate,
                x.EndDate,
                x.CreatedAt
            })
            .ToListAsync(ct);

        contracts = contracts
            .GroupBy(contract => contract.CropTypeId)
            .Select(group => group
                .OrderByDescending(contract => contract.CreatedAt)
                .ThenByDescending(contract => contract.SupplyContractId)
                .First())
            .Where(contract => contract.StartDate <= expectedDeliveryDate
                && contract.EndDate >= expectedDeliveryDate)
            .ToList();

        var result = new List<SupplyContractBatchOption>(contracts.Count);
        foreach (var contract in contracts)
        {
            var (periodStart, periodEnd) = SupplyContractQuotaPeriod.GetBounds(
                contract.StartDate, contract.EndDate, contract.DeliveryPeriod, expectedDeliveryDate);
            var used = await db.ProductBatches.AsNoTracking()
                .Where(x => x.SupplyContractId == contract.SupplyContractId
                    && x.ExpectedDeliveryDate >= periodStart
                    && x.ExpectedDeliveryDate <= periodEnd
                    && x.BatchStatus != "CANCELLED"
                    && x.BatchStatus != "REJECTED")
                .SumAsync(x => (decimal?)x.WeightInKg, ct) ?? 0m;
            result.Add(new SupplyContractBatchOption(
                contract.SupplyContractId,
                contract.ContractCode,
                contract.CropTypeId,
                contract.CropTypeName,
                contract.CategoryName,
                contract.DeliveryQuantityKg,
                used,
                Math.Max(0m, contract.DeliveryQuantityKg - used),
                contract.DeliveryPeriod,
                contract.UnitPriceVndPerKg,
                contract.StartDate,
                contract.EndDate,
                periodStart,
                periodEnd));
        }
        return result;
    }

    public async Task<SupplyContractDetail> CreateAsync(
        int warehouseManagerAccountId, CreateSupplyContractRequest request, CancellationToken ct)
    {
        var supplierExists = await db.Suppliers.AsNoTracking().AnyAsync(x =>
            x.SupplierId == request.SupplierId
            && x.ProfileStatus == "ACTIVE"
            && x.Account.AccountStatus == "ACTIVE", ct);
        if (!supplierExists) throw new NotFoundException("Nhà cung cấp không tồn tại hoặc chưa hoạt động.");
        if (!await db.SupplierCropTypes.AsNoTracking().AnyAsync(x =>
                x.SupplierId == request.SupplierId && x.CropTypeId == request.CropTypeId
                && x.IsActive && x.CropType.IsActive, ct))
            throw new ConflictException("Nhà cung cấp chưa khai báo loại nông sản này trong hồ sơ.");
        if (!await db.CropTypes.AsNoTracking().AnyAsync(x => x.CropTypeId == request.CropTypeId && x.IsActive, ct))
            throw new NotFoundException("Loại nông sản không tồn tại hoặc đã ngừng hoạt động.");

        var contract = new SupplyContract
        {
            ContractCode = $"HD-{DateTime.UtcNow.AddHours(7):yyyyMMdd}-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            WarehouseManagerAccountId = warehouseManagerAccountId,
            SupplierId = request.SupplierId,
            CropTypeId = request.CropTypeId,
            DeliveryQuantityKg = request.DeliveryQuantityKg,
            DeliveryPeriod = request.DeliveryPeriod,
            UnitPriceVndPerKg = request.UnitPriceVndPerKg,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Terms = request.Terms,
            ContractStatus = "PENDING_SUPPLIER",
            Revision = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.SupplyContracts.Add(contract);
        await db.SaveChangesAsync(ct);
        return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", contract.SupplyContractId, ct);
    }

    public async Task<SupplyContractDetail> UpdateByWarehouseAsync(
        int warehouseManagerAccountId, long id, UpdateSupplyContractRequest request, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var contract = await db.SupplyContracts
                .FromSqlInterpolated($"SELECT * FROM dbo.SUPPLY_CONTRACT WITH (UPDLOCK, HOLDLOCK) WHERE SupplyContractID = {id}")
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền cập nhật hợp đồng này.");
            if (contract.WarehouseManagerAccountId != warehouseManagerAccountId)
                throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền cập nhật hợp đồng này.");
            if (contract.ContractStatus is "APPROVED" or "CANCELLATION_REQUESTED" or "CANCELLED")
                throw new ConflictException("Hợp đồng đã được Admin duyệt hoặc đang xử lý hủy nên không thể cập nhật.");

            var batches = await db.ProductBatches.AsNoTracking()
                .Where(x => x.SupplyContractId == id
                    && x.BatchStatus != "CANCELLED"
                    && x.BatchStatus != "REJECTED")
                .Select(x => new { x.ExpectedDeliveryDate, x.WeightInKg })
                .ToListAsync(ct);
            if (batches.Count > 0)
            {
                if (request.StartDate != contract.StartDate || request.DeliveryPeriod != contract.DeliveryPeriod)
                    throw new ConflictException(
                        "Hợp đồng đã có lô hàng nên không thể đổi ngày bắt đầu hoặc chu kỳ giao. Bạn vẫn có thể sửa sản lượng, đơn giá, ngày hết hạn và điều khoản.");
                if (batches.Any(x => x.ExpectedDeliveryDate is null
                    || x.ExpectedDeliveryDate < request.StartDate
                    || x.ExpectedDeliveryDate > request.EndDate))
                    throw new ConflictException("Ngày hết hạn mới không được loại bỏ các lô hàng đã tạo từ hợp đồng.");

                var usedByPeriod = batches
                    .GroupBy(x => SupplyContractQuotaPeriod.GetBounds(
                        request.StartDate, request.EndDate, request.DeliveryPeriod, x.ExpectedDeliveryDate!.Value))
                    .Select(group => new
                    {
                        group.Key.Start,
                        group.Key.End,
                        UsedQuantityKg = group.Sum(x => x.WeightInKg)
                    })
                    .FirstOrDefault(x => x.UsedQuantityKg > request.DeliveryQuantityKg);
                if (usedByPeriod is not null)
                    throw new ConflictException(
                        $"Không thể giảm sản lượng xuống {request.DeliveryQuantityKg:N2} kg vì kỳ {usedByPeriod.Start:dd/MM/yyyy} - {usedByPeriod.End:dd/MM/yyyy} đã tạo {usedByPeriod.UsedQuantityKg:N2} kg.");
            }

            contract.DeliveryQuantityKg = request.DeliveryQuantityKg;
            contract.DeliveryPeriod = request.DeliveryPeriod;
            contract.UnitPriceVndPerKg = request.UnitPriceVndPerKg;
            contract.StartDate = request.StartDate;
            contract.EndDate = request.EndDate;
            contract.Terms = request.Terms;
            contract.Revision += 1;
            contract.ContractStatus = "PENDING_SUPPLIER";
            contract.SupplierRespondedAt = null;
            contract.SupplierRejectionReason = null;
            contract.AdminAccountId = null;
            contract.AdminReviewedAt = null;
            contract.AdminRejectionReason = null;
            contract.CancellationRequestedAt = null;
            contract.CancellationRequestReason = null;
            contract.CancelledAt = null;
            contract.CancellationReason = null;
            contract.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", id, ct);
        });
    }

    public async Task<SupplyContractDetail> RequestCancellationBySupplierAsync(
        int supplierAccountId, long id, string reason, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var contract = await db.SupplyContracts
                .FromSqlInterpolated($"SELECT * FROM dbo.SUPPLY_CONTRACT WITH (UPDLOCK, HOLDLOCK) WHERE SupplyContractID = {id}")
                .Include(x => x.Supplier)
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền yêu cầu hủy.");
            if (contract.Supplier.AccountId != supplierAccountId)
                throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền yêu cầu hủy.");
            if (!SupplyContractCancellationPolicy.CanSupplierRequestCancellation(contract.ContractStatus))
                throw new ConflictException("Chỉ có thể yêu cầu hủy hợp đồng sau khi Admin đã duyệt.");

            contract.ContractStatus = "CANCELLATION_REQUESTED";
            contract.CancellationRequestedAt = DateTime.UtcNow;
            contract.CancellationRequestReason = reason;
            contract.CancellationReason = null;
            contract.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await GetAsync(supplierAccountId, "SUPPLIER", id, ct);
        });
    }

    public async Task<SupplyContractDetail> CancelByWarehouseAsync(
        int warehouseManagerAccountId, long id, string reason, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var contract = await db.SupplyContracts
                .FromSqlInterpolated($"SELECT * FROM dbo.SUPPLY_CONTRACT WITH (UPDLOCK, HOLDLOCK) WHERE SupplyContractID = {id}")
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền hủy hợp đồng này.");
            if (contract.WarehouseManagerAccountId != warehouseManagerAccountId)
                throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền hủy hợp đồng này.");
            if (contract.ContractStatus is "APPROVED" or "CANCELLATION_REJECTED")
            {
                var missedDueDate = await FindLatestMissedDeliveryDueDateAsync(
                    id, contract.ContractStatus, contract.DeliveryQuantityKg, contract.StartDate, contract.EndDate,
                    contract.DeliveryPeriod, ct);
                if (missedDueDate is null)
                    throw new ConflictException("Chỉ có thể hủy hợp đồng khi đã quá hạn và chưa hoàn thành sản lượng cam kết trong kỳ.");

                contract.ContractStatus = "CANCELLED";
                contract.CancelledAt = DateTime.UtcNow;
                contract.CancellationReason = reason;
                contract.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", id, ct);
            }

            if (contract.ContractStatus != "CANCELLATION_REQUESTED")
                throw new ConflictException("Chỉ có thể xử lý yêu cầu hủy của Nhà cung cấp hoặc hủy hợp đồng đã quá hạn giao.");

            contract.ContractStatus = "PENDING_ADMIN_CANCELLATION";
            contract.CancellationReason = reason;
            contract.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", id, ct);
        });
    }

    public async Task<SupplyContractDetail> RejectCancellationByWarehouseAsync(
        int warehouseManagerAccountId, long id, string reason, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var contract = await db.SupplyContracts
                .FromSqlInterpolated($"SELECT * FROM dbo.SUPPLY_CONTRACT WITH (UPDLOCK, HOLDLOCK) WHERE SupplyContractID = {id}")
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền xử lý yêu cầu hủy.");
            if (contract.WarehouseManagerAccountId != warehouseManagerAccountId)
                throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền xử lý yêu cầu hủy.");
            if (contract.ContractStatus != "CANCELLATION_REQUESTED")
                throw new ConflictException("Hợp đồng không còn yêu cầu hủy cần xử lý.");

            contract.ContractStatus = "CANCELLATION_REJECTED";
            contract.CancellationReason = reason;
            contract.CancellationRequestedAt = null;
            contract.CancellationRequestReason = null;
            contract.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await GetAsync(warehouseManagerAccountId, "WAREHOUSE_MANAGER", id, ct);
        });
    }

    public async Task<SupplyContractDetail> CancelByAdminAsync(
        int adminAccountId, long id, CancellationToken ct)
    {
        var contract = await db.SupplyContracts.SingleOrDefaultAsync(x => x.SupplyContractId == id, ct)
            ?? throw new NotFoundException("Không tìm thấy hợp đồng.");
        if (contract.ContractStatus != "PENDING_ADMIN_CANCELLATION")
            throw new ConflictException("Hợp đồng chưa được quản lý kho duyệt yêu cầu hủy.");
        contract.ContractStatus = "CANCELLED";
        contract.AdminAccountId = adminAccountId;
        contract.AdminReviewedAt = DateTime.UtcNow;
        contract.CancelledAt = DateTime.UtcNow;
        contract.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(adminAccountId, "ADMINISTRATOR", id, ct);
    }

    public Task<SupplyContractDetail> ConfirmBySupplierAsync(
        int supplierAccountId, long id, CancellationToken ct) =>
        TransitionAsync(id, supplierAccountId, "SUPPLIER", "PENDING_SUPPLIER", "PENDING_ADMIN", null, ct);

    public Task<SupplyContractDetail> RejectBySupplierAsync(
        int supplierAccountId, long id, string reason, CancellationToken ct) =>
        TransitionAsync(id, supplierAccountId, "SUPPLIER", "PENDING_SUPPLIER", "SUPPLIER_REJECTED", reason, ct);

    public Task<SupplyContractDetail> ApproveByAdminAsync(
        int adminAccountId, long id, CancellationToken ct) =>
        TransitionAsync(id, adminAccountId, "ADMINISTRATOR", "PENDING_ADMIN", "APPROVED", null, ct);

    public Task<SupplyContractDetail> RejectByAdminAsync(
        int adminAccountId, long id, string reason, CancellationToken ct) =>
        TransitionAsync(id, adminAccountId, "ADMINISTRATOR", "PENDING_ADMIN", "ADMIN_REJECTED", reason, ct);

    private async Task<SupplyContractDetail> TransitionAsync(
        long id,
        int actorAccountId,
        string actorRole,
        string expectedStatus,
        string nextStatus,
        string? rejectionReason,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var query = db.SupplyContracts.AsQueryable();
            if (actorRole == "SUPPLIER")
                query = query.Where(x => x.Supplier.AccountId == actorAccountId);
            var contract = await query.SingleOrDefaultAsync(x => x.SupplyContractId == id, ct)
                ?? throw new NotFoundException("Không tìm thấy hợp đồng hoặc bạn không có quyền xử lý hợp đồng này.");
            if (contract.ContractStatus != expectedStatus)
                throw new ConflictException("Hợp đồng đã được xử lý hoặc không còn ở trạng thái phù hợp.");

            var now = DateTime.UtcNow;
            contract.ContractStatus = nextStatus;
            contract.UpdatedAt = now;
            if (actorRole == "SUPPLIER")
            {
                contract.SupplierRespondedAt = now;
                contract.SupplierRejectionReason = nextStatus == "SUPPLIER_REJECTED" ? rejectionReason : null;
            }
            else
            {
                contract.AdminAccountId = actorAccountId;
                contract.AdminReviewedAt = now;
                contract.AdminRejectionReason = nextStatus == "ADMIN_REJECTED" ? rejectionReason : null;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await GetAsync(actorAccountId, actorRole, id, ct);
        });
    }

    private IQueryable<SupplyContract> VisibleContracts(int actorAccountId, string actorRole) => actorRole switch
    {
        "WAREHOUSE_MANAGER" => db.SupplyContracts.Where(x => x.WarehouseManagerAccountId == actorAccountId),
        "SUPPLIER" => db.SupplyContracts.Where(x => x.Supplier.AccountId == actorAccountId),
        "ADMINISTRATOR" => db.SupplyContracts,
        _ => throw new ForbiddenException()
    };

    private async Task<DateOnly?> FindLatestMissedDeliveryDueDateAsync(
        long id, string status, decimal deliveryQuantityKg, DateOnly startDate, DateOnly endDate,
        string deliveryPeriod, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        if (status is not ("APPROVED" or "CANCELLATION_REJECTED")
            || startDate >= today)
            return null;

        DateOnly? latestMissedDueDate = null;
        var periodStart = startDate;
        while (periodStart <= endDate && periodStart < today)
        {
            var periodEnd = SupplyContractQuotaPeriod.GetBounds(
                startDate, endDate, deliveryPeriod, periodStart).End;
            var deliveredKg = await db.GoodsReceipts.AsNoTracking()
                .Where(receipt => receipt.ReceiptStatus == "COMMITTED"
                    && receipt.ProductBatch.SupplyContractId == id
                    && receipt.ProductBatch.ExpectedDeliveryDate >= periodStart
                    && receipt.ProductBatch.ExpectedDeliveryDate <= periodEnd)
                .SumAsync(receipt => (decimal?)receipt.WeightInKg, ct) ?? 0m;
            if (periodEnd < today && deliveredKg < deliveryQuantityKg)
                latestMissedDueDate = periodEnd;

            if (periodEnd >= endDate) break;
            periodStart = periodEnd.AddDays(1);
        }

        return latestMissedDueDate;
    }
}
