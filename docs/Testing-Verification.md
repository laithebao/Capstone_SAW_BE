# Kiểm chứng quy trình test — 14/11/2026

| Môi trường chạy trực tiếp | Profile | Passed | Failed | Skipped |
|---|---|---:|---:|---:|
| macOS ARM64, Node 22.23.3, Chrome, SQL Server 2022 mới | all | 1.546 | 0 | 0 |
| Linux ARM64 trong .NET SDK 8 container | unit | 1.314 | 0 | 0 |
| Linux ARM64 client + SQL Server 2022 qua mạng Docker | integration | 1.536 | 0 | 0 |

Lượt macOS cuối: 11:14–11:18 (UTC+7), khoảng 4 phút 34 giây. Linux integration
hoàn tất khoảng 11:18 (UTC+7). SQL Server x64 chạy giả lập trên Mac ARM trong lượt
local này; không coi đây là xác nhận hỗ trợ chính thức SQL Server trên ARM.

## Điều kiện kiểm chứng

- Bản checkout tạm chỉ lấy file mã nguồn/resources trong Git và những file chuẩn bị
  commit; không có appsettings JSON local, dotenv local cũ hoặc file SQL development
  bên ngoài repo. Runtime source đã được đối chiếu với workspace.
- Init sinh cấu hình và mật khẩu mới; Docker tạo volume SQL mới trên cổng khác,
  không dùng dữ liệu của server đang phát triển.
- Setup tạo database probe, tạo bảng, ghi/đọc rồi xóa để kiểm tra quyền.
- npm ci chạy với Node 22 đúng engines/package-lock của frontend.
- Lượt all chạy hai lần từ SQL mới, gồm browser và upload/download Cloudinary thật.
  Lượt cuối sau sửa phụ thuộc đường dẫn đạt 1.546/1.546.
- Linux unit phát hiện 15 ca file Supplier phụ thuộc tên thư mục workspace. Đã sửa
  dùng thư mục tạm riêng; chạy lại unit và integration đều đạt bảng trên.
- Các 1.537 ca nghiệp vụ trước đó được giữ nguyên về tên method/số ca; thêm 9 ca
  kiểm tra parser dotenv, đường dẫn Windows, giá trị literal và ưu tiên secret CI.
- All thiếu Cloudinary đã được kiểm tra: dừng exit code 1 với thông báo thiếu biến,
  không coi lượt bỏ qua ca external là full passed.
- Compose/YAML workflow parse thành công, git diff --check và cú pháp wrapper bash đạt.
- Docker setup tự lấy số CPU khả dụng, giới hạn tối đa 4 để chạy cả trên runner ít core.

## Bằng chứng local

Các file dưới đây là artifact local, bị Git bỏ qua:

```text
test-results/team-verification/macos-all.trx
test-results/team-verification/macos-all.log
test-results/team-verification/linux-unit.trx
test-results/team-verification/linux-unit.log
test-results/team-verification/linux-integration.trx
test-results/team-verification/linux-integration.log
test-results/team-clean-setup.log
test-results/team-missing-cloudinary.log
```

## Chưa được xác nhận

Windows chưa chạy trực tiếp trong phiên này. Repo hiện không cấu hình CI/CD;
các môi trường chưa được kiểm chứng, gồm Windows và browser trên Linux/Windows,
cần được kiểm tra thủ công trên máy thành viên có dependency tương ứng.

Các file ở cả hai repo BE/FE cần được commit/push trước khi thành viên khác có thể
pull về. Không commit credential, DB dump hay các artifact test ở trên.
