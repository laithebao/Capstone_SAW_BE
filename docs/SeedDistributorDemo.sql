-- Distributor UC 48–51 demo data only. No schema/migration changes.
-- Open this entire file in SSMS, select SmartAgriWarehouseDB, and execute (F5).
-- Demo login: demo_distributor / Demo@12345
-- Second buyer: demo_distributor2 / Demo@12345
-- Re-running preserves existing demo orders, prices, accounts and completed actions.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'SmartAgriWarehouseDB'
    THROW 50300, 'Select SmartAgriWarehouseDB before running the demo seed.', 1;
IF OBJECT_ID(N'dbo.BATCH_SALE_OFFER', N'U') IS NULL OR COL_LENGTH(N'dbo.PURCHASE_ORDER', N'ReceivedAt') IS NULL
    THROW 50301, 'Apply AddDistributorWholeLotOrders with EF Core first.', 1;

DECLARE @Now datetime2(0) = SYSUTCDATETIME();
DECLARE @Today date = CONVERT(date, DATEADD(hour, 7, @Now));
-- Set 1 to seed only the three saleable catalog lots, without sample orders.
DECLARE @CatalogOnly bit = 0;
-- Replaced with a valid PBKDF2 hash supported by the application's password hasher.
DECLARE @PasswordHash nvarchar(255) = N'PBKDF2-SHA256$100000$Opl0sR8cinTytbE2iaQVuA==$0vkX5cJ53eecMpvQC/tmTrkNT5bu0kGblfWLWhGHoFA=';

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @SeedLock int;
    EXEC @SeedLock=sys.sp_getapplock @Resource=N'SAW_DistributorDemoSeed', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
    IF @SeedLock<0 THROW 50302, 'Could not lock demo seed.', 1;

    DECLARE @Accounts TABLE (Username nvarchar(100), RoleCode nvarchar(40), FullName nvarchar(150));
    INSERT @Accounts VALUES
        (N'demo_distributor', N'DISTRIBUTOR', N'DEMO Nhà phân phối 1'),
        (N'demo_distributor2', N'DISTRIBUTOR', N'DEMO Nhà phân phối 2'),
        (N'demo_supplier', N'SUPPLIER', N'DEMO Nhà cung cấp'),
        (N'demo_qc', N'QC_STAFF', N'DEMO Nhân viên QC'),
        (N'demo_operation', N'OPERATION_STAFF', N'DEMO Nhân viên kho'),
        (N'demo_manager1', N'WAREHOUSE_MANAGER', N'DEMO Quản lý kho 1'),
        (N'demo_manager2', N'WAREHOUSE_MANAGER', N'DEMO Quản lý kho 2');
    IF EXISTS (SELECT 1 FROM @Accounts a WHERE NOT EXISTS (SELECT 1 FROM dbo.ROLE r WHERE r.RoleCode=a.RoleCode AND r.IsActive=1))
        THROW 50303, 'Required active roles are missing. Seed normal application roles first.', 1;
    IF EXISTS (SELECT 1 FROM @Accounts d JOIN dbo.ACCOUNT a ON a.Username=d.Username OR a.Email=d.Username+N'@demo.saw.test'
        JOIN dbo.ROLE r ON r.RoleID=a.RoleID
        WHERE a.Username<>d.Username OR a.Email<>d.Username+N'@demo.saw.test' OR r.RoleCode<>d.RoleCode)
        THROW 50304, 'A demo username/email conflicts with an existing account. No data changed.', 1;
    INSERT dbo.ACCOUNT (RoleID, Username, Email, PasswordHash, FullName, AccountStatus, CreatedAt)
    SELECT r.RoleID, a.Username, a.Username+N'@demo.saw.test', @PasswordHash, a.FullName, N'ACTIVE', @Now
    FROM @Accounts a JOIN dbo.ROLE r ON r.RoleCode=a.RoleCode
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ACCOUNT existing WHERE existing.Username=a.Username);

    DECLARE @Buyer int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_distributor');
    DECLARE @Buyer2 int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_distributor2');
    DECLARE @SupplierAccount int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_supplier');
    DECLARE @Qc int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_qc');
    DECLARE @Operation int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_operation');
    DECLARE @Manager1 int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_manager1');
    DECLARE @Manager2 int=(SELECT AccountID FROM dbo.ACCOUNT WHERE Username=N'demo_manager2');
    EXEC sys.sp_set_session_context @key=N'AccountID', @value=@Operation;

    INSERT dbo.DISTRIBUTOR (AccountID, DistributorCode, DistributorName, TaxCode, ContactPerson, PhoneNumber, Email, Address, ProfileStatus, CreatedAt)
    SELECT a.AccountID, CASE WHEN a.AccountID=@Buyer THEN N'DEMO-DIST-BUYER1' ELSE N'DEMO-DIST-BUYER2' END,
        a.FullName, CASE WHEN a.AccountID=@Buyer THEN N'DEMO-DIST-TAX-BUYER1' ELSE N'DEMO-DIST-TAX-BUYER2' END,
        a.FullName, N'0901234567', a.Email, N'10 Nguyễn Văn Linh, Đà Nẵng (demo)', N'ACTIVE', @Now
    FROM dbo.ACCOUNT a WHERE a.AccountID IN (@Buyer,@Buyer2)
        AND NOT EXISTS (SELECT 1 FROM dbo.DISTRIBUTOR d WHERE d.AccountID=a.AccountID);
    DECLARE @Distributor int=(SELECT DistributorID FROM dbo.DISTRIBUTOR WHERE AccountID=@Buyer);
    DECLARE @Distributor2 int=(SELECT DistributorID FROM dbo.DISTRIBUTOR WHERE AccountID=@Buyer2);

    IF NOT EXISTS (SELECT 1 FROM dbo.SUPPLIER WHERE AccountID=@SupplierAccount)
        INSERT dbo.SUPPLIER (AccountID, SupplierCode, SupplierName, TaxCode, ContactPerson, PhoneNumber, Email, Address, Note, ProfileStatus, CreatedAt)
        VALUES (@SupplierAccount,N'DEMO-DIST-SUPPLIER',N'HTX nông sản demo',N'DEMO-DIST-TAX-SUPPLIER',N'Nhà cung cấp demo',N'0901111222',N'demo_supplier@demo.saw.test',N'Vùng trồng demo, Đà Nẵng',N'Dữ liệu test Distributor',N'ACTIVE',@Now);
    DECLARE @Supplier int=(SELECT SupplierID FROM dbo.SUPPLIER WHERE AccountID=@SupplierAccount);

    IF NOT EXISTS (SELECT 1 FROM dbo.GROWING_AREA WHERE AreaName=N'DEMO-DIST-Vùng trồng')
        INSERT dbo.GROWING_AREA (AreaName,Region,Province,District,Ward,Description)
        VALUES (N'DEMO-DIST-Vùng trồng',N'Miền Trung',N'Đà Nẵng',N'Hòa Vang',N'Hòa Phong',N'Vùng trồng dùng riêng cho demo Distributor');
    DECLARE @Area int=(SELECT MIN(GrowingAreaId) FROM dbo.GROWING_AREA WHERE AreaName=N'DEMO-DIST-Vùng trồng');
    IF NOT EXISTS (SELECT 1 FROM dbo.SUPPLIER_GROWING_AREA WHERE SupplierID=@Supplier AND GrowingAreaId=@Area)
        INSERT dbo.SUPPLIER_GROWING_AREA (SupplierID,GrowingAreaId) VALUES (@Supplier,@Area);

    IF NOT EXISTS (SELECT 1 FROM dbo.WAREHOUSE_LOCATION WHERE LocationCode=N'DEMO-DIST-ZONE')
        INSERT dbo.WAREHOUSE_LOCATION (LocationCode,ZoneName,MaxWeightKg,LocationStatus,CreatedAt)
        VALUES (N'DEMO-DIST-ZONE',N'Khu demo Distributor',1000,N'ACTIVE',@Now);
    DECLARE @Location int=(SELECT WarehouseLocationID FROM dbo.WAREHOUSE_LOCATION WHERE LocationCode=N'DEMO-DIST-ZONE');

    DECLARE @Crops TABLE (Code nvarchar(30), Name nvarchar(100), Category nvarchar(100));
    INSERT @Crops VALUES (N'DEMO-DIST-RICE',N'Gạo demo',N'Ngũ cốc'),(N'DEMO-DIST-MANGO',N'Xoài demo',N'Trái cây'),(N'DEMO-DIST-DRAGON',N'Thanh long demo',N'Trái cây');
    INSERT dbo.CROP_TYPE (CropCode,CropName,CategoryName,DefaultUnit,IsActive,CreatedAt)
    SELECT Code,Name,Category,N'kg',1,@Now FROM @Crops c WHERE NOT EXISTS (SELECT 1 FROM dbo.CROP_TYPE e WHERE e.CropCode=c.Code);
    INSERT dbo.SUPPLIER_CROP_TYPE (SupplierID,CropTypeID)
    SELECT @Supplier,c.CropTypeID FROM dbo.CROP_TYPE c JOIN @Crops d ON d.Code=c.CropCode
    WHERE NOT EXISTS (SELECT 1 FROM dbo.SUPPLIER_CROP_TYPE e WHERE e.SupplierID=@Supplier AND e.CropTypeID=c.CropTypeID);

    -- Standards include two distinct manager reviews before publishing, respecting the existing trigger.
    DECLARE @Crop int,@CropCode nvarchar(30),@Set int,@Version bigint,@Criterion bigint;
    DECLARE crop_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT c.CropTypeID,c.CropCode FROM dbo.CROP_TYPE c JOIN @Crops d ON d.Code=c.CropCode;
    OPEN crop_cursor; FETCH NEXT FROM crop_cursor INTO @Crop,@CropCode;
    WHILE @@FETCH_STATUS=0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.INSPECTION_STANDARD_SET WHERE StandardCode=@CropCode)
            INSERT dbo.INSPECTION_STANDARD_SET (CropTypeID,StandardCode,StandardName,Description,IsActive,CreatedAt)
            VALUES (@Crop,@CropCode,N'Tiêu chuẩn demo Distributor',N'Dữ liệu demo, không phải tiêu chuẩn sản xuất',1,@Now);
        SELECT @Set=InspectionStandardSetID FROM dbo.INSPECTION_STANDARD_SET WHERE StandardCode=@CropCode;
        IF NOT EXISTS (SELECT 1 FROM dbo.INSPECTION_STANDARD_VERSION WHERE InspectionStandardSetID=@Set AND VersionNo=1)
        BEGIN
            INSERT dbo.INSPECTION_STANDARD_VERSION (InspectionStandardSetID,VersionNo,VersionStatus,EffectiveFrom,CreatedAt)
            VALUES (@Set,1,N'DRAFT',@Today,@Now);
            SET @Version=SCOPE_IDENTITY();
            INSERT dbo.INSPECTION_CRITERION (InspectionStandardVersionID,CriterionCode,CriterionName,CriterionGroup,DataType,Unit,IsRequired,IsCritical)
            VALUES (@Version,N'DEMO-SCORE',N'Điểm kiểm định demo',N'SENSORY',N'NUMBER',N'điểm',1,0);
            SET @Criterion=SCOPE_IDENTITY();
            INSERT dbo.CRITERION_GRADE_RULE (InspectionCriterionID,Grade,MinValue,MaxValue,IsFailRule)
            VALUES (@Criterion,N'A',0,100,0);
            INSERT dbo.STANDARD_VERSION_REVIEW (InspectionStandardVersionID,ReviewedByAccountID,ReviewSequence,ReviewDecision,Comments,ReviewedAt)
            VALUES (@Version,@Manager1,1,N'APPROVED',N'Duyệt dữ liệu demo',@Now),(@Version,@Manager2,2,N'APPROVED',N'Duyệt dữ liệu demo',@Now);
            UPDATE dbo.INSPECTION_STANDARD_VERSION SET VersionStatus=N'PUBLISHED' WHERE InspectionStandardVersionID=@Version;
        END;
        FETCH NEXT FROM crop_cursor INTO @Crop,@CropCode;
    END;
    CLOSE crop_cursor; DEALLOCATE crop_cursor;

    DECLARE @Lots TABLE (No int, CropCode nvarchar(30), Product nvarchar(200), WeightKg decimal(18,3), Price decimal(18,2), Expired bit, PublishPrice bit);
    INSERT @Lots VALUES
        (1,N'DEMO-DIST-RICE',N'Gạo ST25 — lô demo 01',100,1500000,0,1),
        (2,N'DEMO-DIST-MANGO',N'Xoài cát — lô demo 02',80,2400000,0,1),
        (3,N'DEMO-DIST-DRAGON',N'Thanh long — lô demo 03',60,1200000,0,1),
        (4,N'DEMO-DIST-RICE',N'Gạo — đơn chờ duyệt demo',30,450000,0,1),
        (5,N'DEMO-DIST-MANGO',N'Xoài — lô đã giữ hàng demo',40,1200000,0,1),
        (6,N'DEMO-DIST-RICE',N'Gạo — lô đang giao demo',50,750000,0,1),
        (7,N'DEMO-DIST-DRAGON',N'Thanh long — đơn bị từ chối demo',25,500000,0,1),
        (8,N'DEMO-DIST-DRAGON',N'Thanh long — lô hết hạn demo',20,400000,1,1),
        (9,N'DEMO-DIST-MANGO',N'Xoài — lô chưa công bố giá demo',15,450000,0,0);

    DECLARE @No int,@Product nvarchar(200),@Weight decimal(18,3),@Price decimal(18,2),@Expired bit,@Publish bit,@Batch bigint,@Code nvarchar(50);
    DECLARE lot_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT No,CropCode,Product,WeightKg,Price,Expired,PublishPrice FROM @Lots WHERE @CatalogOnly=0 OR No IN (1,2,3) ORDER BY No;
    OPEN lot_cursor; FETCH NEXT FROM lot_cursor INTO @No,@CropCode,@Product,@Weight,@Price,@Expired,@Publish;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SET @Code=N'DEMO-DIST-LOT-'+RIGHT(N'0'+CONVERT(nvarchar(2),@No),2);
        SELECT @Crop=CropTypeID FROM dbo.CROP_TYPE WHERE CropCode=@CropCode;
        SELECT @Version=v.InspectionStandardVersionID FROM dbo.INSPECTION_STANDARD_VERSION v JOIN dbo.INSPECTION_STANDARD_SET s ON s.InspectionStandardSetID=v.InspectionStandardSetID WHERE s.StandardCode=@CropCode AND v.VersionNo=1;
        IF NOT EXISTS (SELECT 1 FROM dbo.PRODUCT_BATCH WHERE BatchCode=@Code)
            INSERT dbo.PRODUCT_BATCH (BatchCode,SupplierID,CropTypeID,GrowingAreaID,ProductName,HarvestDate,DeclaredQuantity,Unit,WeightInKg,VerifiedQuantity,VerifiedWeightInKg,ExpiryDate,BatchStatus,QualityGrade,Note,CreatedAt)
            VALUES (@Code,@Supplier,@Crop,@Area,@Product,DATEADD(day,-20,@Today),@Weight,N'kg',@Weight,@Weight,@Weight,CASE WHEN @Expired=1 THEN DATEADD(day,-1,@Today) ELSE DATEADD(day,30,@Today) END,N'IN_STOCK',N'A',N'Dữ liệu test Distributor',DATEADD(day,-12,@Now));
        SELECT @Batch=ProductBatchID FROM dbo.PRODUCT_BATCH WHERE BatchCode=@Code;
        IF NOT EXISTS (SELECT 1 FROM dbo.QC_INSPECTION WHERE InspectionCode=N'DEMO-QC-'+@Code)
            INSERT dbo.QC_INSPECTION (InspectionCode,ProductBatchID,InspectionStandardVersionID,QCAccountID,SamplingRatio,SampleSize,InspectionStatus,QCResult,QualityGrade,StartedAt,CompletedAt,Note)
            VALUES (N'DEMO-QC-'+@Code,@Batch,@Version,@Qc,0.1,@Weight*0.1,N'COMPLETED',N'PASS',N'A',DATEADD(day,-11,@Now),DATEADD(hour,1,DATEADD(day,-11,@Now)),N'QC demo đã hoàn tất');
        IF NOT EXISTS (SELECT 1 FROM dbo.INVENTORY WHERE ProductBatchID=@Batch AND WarehouseLocationID=@Location)
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.WAREHOUSE_SETTING WHERE MaxCapacityKg IS NOT NULL
                AND MaxCapacityKg < COALESCE((SELECT SUM(QuantityOnHand) FROM dbo.INVENTORY),0)+@Weight)
                THROW 50305, 'Not enough warehouse capacity for demo stock. Reduce the demo weights first.', 1;
            INSERT dbo.INVENTORY (ProductBatchID,WarehouseLocationID,QuantityOnHand,ReservedQuantity,Unit,LastUpdatedAt)
            VALUES (@Batch,@Location,@Weight,0,N'kg',@Now);
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.GOODS_RECEIPT WHERE ReceiptCode=N'DEMO-GR-'+@Code)
            INSERT dbo.GOODS_RECEIPT (ReceiptCode,ProductBatchID,WarehouseLocationID,OperationAccountID,ReceivedQuantity,Unit,WeightInKg,ReceiptStatus,ReceivedAt,CommittedAt,Note)
            VALUES (N'DEMO-GR-'+@Code,@Batch,@Location,@Operation,@Weight,N'kg',@Weight,N'COMMITTED',DATEADD(day,-10,@Now),DATEADD(day,-10,@Now),N'Nhập kho demo');
        IF @Publish=1 AND NOT EXISTS (SELECT 1 FROM dbo.BATCH_SALE_OFFER WHERE ProductBatchID=@Batch)
            INSERT dbo.BATCH_SALE_OFFER (ProductBatchID,WholeLotPrice,IsPublished,UpdatedAt) VALUES (@Batch,@Price,1,@Now);
        FETCH NEXT FROM lot_cursor INTO @No,@CropCode,@Product,@Weight,@Price,@Expired,@Publish;
    END;
    CLOSE lot_cursor; DEALLOCATE lot_cursor;

    DECLARE @Orders TABLE (Code nvarchar(50), LotNo int, Buyer int, TargetStatus nvarchar(30));
    IF @CatalogOnly=0
    INSERT @Orders VALUES (N'DEMO-DIST-PO-PENDING',4,@Distributor,N'PENDING'),
        (N'DEMO-DIST-PO-RESERVED',5,@Distributor,N'RESERVED'),
        (N'DEMO-DIST-PO-DISPATCHED',6,@Distributor,N'DISPATCHED'),
        (N'DEMO-DIST-PO-REJECTED',7,@Distributor,N'REJECTED'),
        (N'DEMO-DIST-PO-OTHER',1,@Distributor2,N'PENDING');
    DECLARE @OrderCode nvarchar(50),@Owner int,@Status nvarchar(30),@Order bigint,@Line bigint,@Inventory bigint,@Reservation bigint,@Issue bigint;
    DECLARE order_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code,LotNo,Buyer,TargetStatus FROM @Orders;
    OPEN order_cursor; FETCH NEXT FROM order_cursor INTO @OrderCode,@No,@Owner,@Status;
    WHILE @@FETCH_STATUS=0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.PURCHASE_ORDER WHERE OrderCode=@OrderCode)
        BEGIN
            SELECT @Weight=WeightKg,@Price=Price,@CropCode=CropCode FROM @Lots WHERE No=@No;
            SELECT @Batch=ProductBatchID,@Crop=CropTypeID FROM dbo.PRODUCT_BATCH WHERE BatchCode=N'DEMO-DIST-LOT-'+RIGHT(N'0'+CONVERT(nvarchar(2),@No),2);
            SELECT @Inventory=InventoryID FROM dbo.INVENTORY WHERE ProductBatchID=@Batch AND WarehouseLocationID=@Location;
            DECLARE @OwnerAccount int=(SELECT AccountID FROM dbo.DISTRIBUTOR WHERE DistributorID=@Owner);
            EXEC sys.sp_set_session_context @key=N'AccountID', @value=@OwnerAccount;
            INSERT dbo.PURCHASE_ORDER (OrderCode,DistributorID,OrderStatus,DeliveryAddress,ContactPhone,ExpectedDeliveryDate,OrderNote,SubtotalAmount,TaxAmount,TotalAmount,CreatedAt)
            VALUES (@OrderCode,@Owner,N'PENDING',N'10 Nguyễn Văn Linh, Đà Nẵng (demo)',N'0901234567',DATEADD(day,2,@Today),N'Đơn mẫu để test Distributor',@Price,0,@Price,DATEADD(hour,-6,@Now));
            SET @Order=SCOPE_IDENTITY();
            INSERT dbo.ORDER_DETAIL (PurchaseOrderID,CropTypeID,RequestedProductBatchID,RequestedQuantity,ApprovedQuantity,Unit,RequestedWeightKg,ApprovedWeightKg,ReservedWeightKg,PickedWeightKg,UnitPrice,TaxAmount)
            VALUES (@Order,@Crop,@Batch,1,0,N'Lô',@Weight,0,0,0,@Price,0);
            SET @Line=SCOPE_IDENTITY();
            IF @Status=N'REJECTED'
            BEGIN
                EXEC sys.sp_set_session_context @key=N'AccountID', @value=@Manager1;
                UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'REJECTED',RejectedAt=@Now,RejectionReason=N'Demo: chưa thống nhất lịch giao hàng',UpdatedAt=@Now WHERE PurchaseOrderID=@Order;
            END;
            IF @Status IN (N'RESERVED',N'DISPATCHED')
            BEGIN
                EXEC sys.sp_set_session_context @key=N'AccountID', @value=@Manager1;
                UPDATE dbo.ORDER_DETAIL SET ApprovedQuantity=1,ApprovedWeightKg=@Weight WHERE OrderDetailID=@Line;
                UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'APPROVED',ApprovedByAccountID=@Manager1,ApprovedAt=@Now,UpdatedAt=@Now WHERE PurchaseOrderID=@Order;
                INSERT dbo.INVENTORY_RESERVATION (OrderDetailID,InventoryID,ReservedQuantity,PickedQuantity,ReservationStatus,ReservedAt)
                VALUES (@Line,@Inventory,@Weight,0,N'RESERVED',@Now);
                SET @Reservation=SCOPE_IDENTITY();
                UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'RESERVED' WHERE PurchaseOrderID=@Order;
                UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'RESERVED',UpdatedAt=@Now WHERE ProductBatchID=@Batch;
                IF @Status=N'DISPATCHED'
                BEGIN
                    EXEC sys.sp_set_session_context @key=N'AccountID', @value=@Operation;
                    INSERT dbo.PICKING_HISTORY (InventoryReservationID,PickedByAccountID,PickedQuantity,PickedAt,Note)
                    VALUES (@Reservation,@Operation,@Weight,@Now,N'Đã lấy nguyên lô demo');
                    UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'PICKED' WHERE PurchaseOrderID=@Order;
                    -- Insert detail while issue is DRAFT; the production trigger rejects details added after COMMITTED.
                    INSERT dbo.GOODS_ISSUE (IssueCode,PurchaseOrderID,OperationAccountID,ReceiverName,IssueStatus,IssuedAt,Note)
                    VALUES (N'DEMO-DIST-GI-06',@Order,@Operation,N'Nhà phân phối demo',N'DRAFT',@Now,N'Xuất nguyên lô demo');
                    SET @Issue=SCOPE_IDENTITY();
                    INSERT dbo.GOODS_ISSUE_DETAIL (GoodsIssueID,OrderDetailID,InventoryReservationID,InventoryID,IssuedQuantity,Unit,WeightInKg)
                    VALUES (@Issue,@Line,@Reservation,@Inventory,@Weight,N'kg',@Weight);
                    UPDATE dbo.GOODS_ISSUE SET IssueStatus=N'COMMITTED',CommittedAt=@Now WHERE GoodsIssueID=@Issue;
                    -- Clear allocation counters after issuing. Immutable PICKING_HISTORY preserves the full picked weight.
                    -- The current sync trigger excludes ISSUED from reserved weight, so picked allocation must also be zero
                    -- to satisfy CK_ORDER_DETAIL_Weight (PickedWeightKg <= ReservedWeightKg).
                    UPDATE dbo.INVENTORY_RESERVATION SET ReservationStatus=N'ISSUED',PickedQuantity=0,ReleasedAt=@Now WHERE InventoryReservationID=@Reservation;
                    UPDATE dbo.INVENTORY SET QuantityOnHand=0,LastUpdatedAt=@Now WHERE InventoryID=@Inventory;
                    UPDATE dbo.PRODUCT_BATCH SET BatchStatus=N'ISSUED',UpdatedAt=@Now WHERE ProductBatchID=@Batch;
                    UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'ISSUED' WHERE PurchaseOrderID=@Order;
                    UPDATE dbo.PURCHASE_ORDER SET OrderStatus=N'DISPATCHED',UpdatedAt=@Now WHERE PurchaseOrderID=@Order;
                END;
            END;
        END;
        FETCH NEXT FROM order_cursor INTO @OrderCode,@No,@Owner,@Status;
    END;
    CLOSE order_cursor; DEALLOCATE order_cursor;
    EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL;
    THROW;
END CATCH;

SELECT a.Username,a.Email,r.RoleCode FROM dbo.ACCOUNT a JOIN dbo.ROLE r ON r.RoleID=a.RoleID WHERE a.Username IN (N'demo_distributor',N'demo_distributor2');
SELECT o.OrderCode,o.OrderStatus,o.TotalAmount,a.Username AS Owner FROM dbo.PURCHASE_ORDER o JOIN dbo.DISTRIBUTOR d ON d.DistributorID=o.DistributorID JOIN dbo.ACCOUNT a ON a.AccountID=d.AccountID WHERE o.OrderCode LIKE N'DEMO-DIST-PO-%' ORDER BY o.OrderCode;
SELECT b.BatchCode,b.ProductName,b.BatchStatus,b.ExpiryDate,i.QuantityOnHand,i.ReservedQuantity,i.AvailableQuantity,p.WholeLotPrice,p.IsPublished
FROM dbo.PRODUCT_BATCH b JOIN dbo.INVENTORY i ON i.ProductBatchID=b.ProductBatchID LEFT JOIN dbo.BATCH_SALE_OFFER p ON p.ProductBatchID=b.ProductBatchID WHERE b.BatchCode LIKE N'DEMO-DIST-LOT-%' ORDER BY b.BatchCode;
