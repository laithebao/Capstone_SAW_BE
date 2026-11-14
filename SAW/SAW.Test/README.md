# SAW.Test

**Bắt đầu tại [hướng dẫn test Windows/macOS/Linux cho cả nhóm](../../docs/Testing.md).**

Từ thư mục backend, các lệnh giống nhau trên PowerShell, CMD, bash và zsh:

```text
dotnet run --project tools/SAW.Testing -- test --profile unit
dotnet run --project tools/SAW.Testing -- init
dotnet run --project tools/SAW.Testing -- setup --docker --profile browser
dotnet run --project tools/SAW.Testing -- test --profile browser
```

| Profile | Được chọn | Cần |
|---|---|---|
| unit | Unit/InMemory/HTTP mock | .NET 8 SDK |
| integration | Unit + SQL/API thật | SQL Server test |
| browser | Integration + frontend thật | SQL, FE, Node 22, Chrome/Edge |
| all | Toàn bộ, gồm Cloudinary thật | Thêm 3 thông tin Cloudinary test |

Runner chọn test qua trait RequiresSql/RequiresBrowser/RequiresCloudinary. Lượt có
0 test hoặc test được chọn nhưng skipped không được chấp nhận là thành công.
Profile browser không kiểm chứng Cloudinary thật. Test AI dùng phản hồi model giả lập,
không cần API key AI thật. Kết quả: `test-results/<profile>/<run>/`.

Khi thêm test SQL/browser/Cloudinary, dùng các attribute opt-in tương ứng như
`ReceivingSqlFact`, `ReceivingSqlTheory`, `SupplierSqlFact`, `DistributorBrowserFact`,
`GoodsReceiptBrowserFact`, `RealCloudinaryFact`; trait của profile được gắn tự động.

## Schema và dữ liệu

Chỉ dùng `SAW_TEST_SQL_CONNECTION`; `Database=master` là hợp lệ. Fixture chỉ lấy
server/tài khoản, tạo database GUID riêng, tạo schema/seed và dọn đúng database sở hữu.
Supplier dùng `SAW_<GUID>_SupplierTests` để bộ đếm không bị ảnh hưởng giữa các lượt.
Không đọc trigger hoặc reset dữ liệu database development.

Các định nghĩa được version trong Git và nhúng vào assembly:

- Infrastructure/GoodsReceipts/GoodsReceiptSchema.sql
- Infrastructure/Picking/PickingSchema.sql
- Infrastructure/GoodsIssues/GoodsIssueSchema.sql
- Infrastructure/StockAdjustments/StockAdjustmentSchema.sql

Khi sửa nghiệp vụ/schema, review các resource này cùng test. Fixture migration vẫn
chạy migration thật trên baseline cô lập. `InitialCreate.Up` trống nên không dùng
`dotnet ef database update` như cách tự tạo baseline, không tự chèn migration history.

`xunit.runner.json` cho các lớp chạy tuần tự; tác vụ concurrency bên trong các test
vẫn chạy như trước. Cleanup chỉ clear pool của database sở hữu. ReceivingSqlFixture
giữ lỗi cleanup trong `Exception.Data["SqlFixtureCleanupError"]` khi khởi tạo lỗi.
API test tắt QR upload, AI credentials và dùng email log, không cần appsettings local.

## Chạy một nhóm

```text
dotnet run --project tools/SAW.Testing -- test --profile integration --filter "FullyQualifiedName~SupplierSqlIntegrationTests"
dotnet run --project tools/SAW.Testing -- test --profile integration --filter "FullyQualifiedName~Infrastructure.AiChat|FullyQualifiedName~Infrastructure.AiForecast"
```

Filter bổ sung được giao với filter profile. Muốn chạy ca browser, chọn browser/all.
Wrapper `scripts/test-local.sh` và `scripts/test-local.ps1` gọi profile all, vì vậy cần
Node 22 và Cloudinary đã cấu hình. Dùng CLI ở trên cho các profile khác.
