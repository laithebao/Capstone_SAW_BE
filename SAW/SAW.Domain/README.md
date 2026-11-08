# SAW.Domain – Domain Layer

> **Tầng lõi (Core) của hệ thống. Không phụ thuộc vào bất kỳ project nào khác.**

---

## 📌 Vai trò

- Chứa toàn bộ **domain entities** ánh xạ 1-1 với các bảng trong database.
- Định nghĩa **common types** dùng chung cho toàn hệ thống.
- Là nền tảng mà tất cả các layer khác đều tham chiếu.

---

## 📁 Cấu trúc thư mục

```
SAW.Domain/
├── Entities/                    ← Domain entities (42 entities)
│   ├── Account.cs               ← Tài khoản người dùng
│   ├── Role.cs                  ← Vai trò (Admin, Manager, Staff, ...)
│   ├── Permission.cs            ← Quyền truy cập
│   ├── AccountPermission.cs     ← Phân quyền theo account
│   ├── RefreshToken.cs          ← JWT refresh token
│   ├── EmailVerificationToken.cs
│   ├── PasswordResetToken.cs
│   │
│   ├── Supplier.cs              ← Nhà cung cấp
│   ├── SupplierCertification.cs ← Chứng nhận nhà cung cấp
│   ├── SupplierCropType.cs      ← Loại nông sản nhà cung cấp cung cấp
│   ├── Distributor.cs           ← Đơn vị phân phối
│   │
│   ├── CropType.cs              ← Loại nông sản (lúa, ngô, ...)
│   ├── ProductBatch.cs          ← Lô hàng nông sản
│   ├── Inventory.cs             ← Tồn kho
│   ├── InventoryReservation.cs  ← Đặt trước tồn kho
│   ├── WarehouseLocation.cs     ← Vị trí kho (khu, hàng, ô)
│   ├── WarehouseSetting.cs      ← Cài đặt kho
│   │
│   ├── PurchaseOrder.cs         ← Đơn mua hàng
│   ├── OrderDetail.cs           ← Chi tiết đơn hàng
│   ├── OrderStatusHistory.cs    ← Lịch sử trạng thái đơn hàng
│   ├── GoodsReceipt.cs          ← Phiếu nhập kho
│   ├── GoodsIssue.cs            ← Phiếu xuất kho
│   ├── GoodsIssueDetail.cs      ← Chi tiết phiếu xuất
│   ├── StockAdjustment.cs       ← Điều chỉnh tồn kho
│   ├── StockTransfer.cs         ← Chuyển kho
│   ├── PickingHistory.cs        ← Lịch sử lấy hàng
│   ├── BatchStatusHistory.cs    ← Lịch sử trạng thái lô hàng
│   │
│   ├── QcInspection.cs          ← Phiếu kiểm tra chất lượng (QC)
│   ├── InspectionCriterion.cs   ← Tiêu chí kiểm tra
│   ├── InspectionResultDetail.cs← Kết quả kiểm tra chi tiết
│   ├── InspectionStandardSet.cs ← Bộ tiêu chuẩn kiểm tra
│   ├── InspectionStandardVersion.cs
│   ├── StandardVersionReview.cs
│   ├── CriterionGradeRule.cs    ← Quy tắc xếp loại theo tiêu chí
│   ├── LabResult.cs             ← Kết quả kiểm nghiệm phòng lab
│   ├── SensoryResult.cs         ← Đánh giá cảm quan
│   ├── QualityImage.cs          ← Ảnh chất lượng
│   │
│   ├── EnvironmentLog.cs        ← Log môi trường (nhiệt độ, độ ẩm)
│   ├── AiRiskForecast.cs        ← Dự báo rủi ro bằng AI
│   ├── AuditLog.cs              ← Log kiểm toán hệ thống
│   ├── Notification.cs          ← Thông báo
│   └── QrCode.cs                ← Mã QR cho lô hàng
│
└── Common/
    └── ApiResponse.cs           ← Generic response wrapper cho toàn bộ API
```

---

## 📄 `Common/ApiResponse<T>`

Chuẩn hoá format JSON response cho toàn bộ hệ thống.

### Cấu trúc response

```json
{
  "statusCode": 200,
  "isSuccess": true,
  "message": "Success",
  "data": { ... },
  "errors": null,
  "timestamp": "2026-09-13T14:00:00Z"
}
```

### Cách dùng trong Controller

```csharp
// ✅ 200 OK
return Ok(ApiResponse<UserDto>.Success(dto));

// ✅ 201 Created
return CreatedAtAction(nameof(GetById), new { id }, ApiResponse<UserDto>.Created(dto));

// ✅ 204 No Content (delete)
return Ok(ApiResponse.NoContent("Deleted successfully"));

// ✅ 404 Not Found
return NotFound(ApiResponse<UserDto>.NotFound("User not found"));
```

---

## 📦 Dependencies

Không có dependency vào project nào khác trong solution. Là **Pure C# class library**.
