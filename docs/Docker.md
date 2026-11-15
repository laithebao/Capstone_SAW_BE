# Bước 2 — Chuẩn bị backend .NET 8 chạy bằng Docker

Thực hiện tại root repo backend, không phải root thư mục `D:\Capstone`.

Các script hỗ trợ Windows PowerShell 5.1 có sẵn trên Windows và PowerShell 7. Trong cửa sổ PowerShell đang mở, chạy trực tiếp file `.ps1`; không cần lệnh `pwsh`.

Nếu báo `pwsh is not recognized`, dùng Windows PowerShell có sẵn. Nếu policy của máy chặn chạy file `.ps1`, lệnh sau chỉ đặt execution policy cho tiến trình kiểm tra này, không đổi policy lâu dài:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-container.ps1 -Image saw-api:step2
```

```powershell
cd D:\Capstone\BE\Capstone_SAW_BE
```

## 1. Những gì được chuẩn bị

- `Dockerfile`: build Release bằng SDK 8, runtime ASP.NET 8 Debian Bookworm, user `app`, cổng HTTP 8080.
- `.dockerignore`: chỉ gửi source API và ba project tham chiếu; chặn appsettings local, dotenv, file upload, key, build output và IDE cache.
- `.env.container.example` và `scripts/init-container.ps1`: tạo file cấu hình local có JWT key ngẫu nhiên, không ghi key ra console và không ghi đè file đã có.
- `GET /health/live`: process sống, không phụ thuộc DB/dịch vụ ngoài.
- `GET /health/ready`: kiểm tra SQL, các bảng lõi và migration cuối mà binary yêu cầu. Trả 503 khi chưa sẵn sàng; response không công khai lỗi SQL hay connection string.
- Health response có `release` để xác nhận commit khi phát hành.

Target framework và các package nghiệp vụ vẫn giữ nguyên .NET 8. Bước này không tạo resource Azure và không tự sửa/seed/migrate database.

## 2. Bật Docker Desktop và tạo cấu hình local

Mở Docker Desktop, chờ Linux Engine sẵn sàng. Kiểm tra:

```powershell
docker version
```

Phải thấy cả Client và Server. Chỉ thấy Client hoặc lỗi `dockerDesktopLinuxEngine` nghĩa là engine chưa sẵn sàng.

Tạo cấu hình:

```powershell
.\scripts\init-container.ps1
```

File `.env.container.local` được Git bỏ qua. Mở file bằng editor và thay SQL user/password/server/database. Không gửi nội dung file hoặc credential trong chat. Docker `--env-file` không cần dấu nháy bọc giá trị.

- SQL chạy trên host Docker Desktop: có thể dùng `host.docker.internal,<port>`.
- SQL ở container khác: cần network chung và tên service/container, không dùng `localhost`.
- SQL trong `compose.test.yml` hiện bind vào loopback host và dành cho fixture test; không coi các database fixture tự tạo là database app lâu dài.
- Giữ TLS/certificate phù hợp. Chỉ dùng `TrustServerCertificate=True` cho SQL local tự ký khi đã xác định đúng server; Azure SQL dùng `Encrypt=True;TrustServerCertificate=False`.

Giữ QR/OpenAI/Qdrant tắt trong file mẫu khi chưa kiểm thử các dịch vụ này. `Email__DeliveryMode=Log` chỉ dành cho local có kiểm soát vì email verification/reset links chứa token.

## 3. Build image

```powershell
$commit = git rev-parse HEAD
docker build --platform linux/amd64 --build-arg "RELEASE_COMMIT=$commit" -t saw-api:local .
```

Trong lúc có thay đổi chưa commit, SHA chỉ đại diện commit nền; không coi đây là release có thể promote. Khi phát hành phải build từ commit đã chốt.

Có thể kiểm tra image bằng cấu hình dùng một lần, không đọc credential local và không cần database thật:

```powershell
.\scripts\verify-container.ps1 -Image saw-api:local
```

Script kiểm tra non-root, health/live, readiness 503 khi SQL cố tình không truy cập được, KnowledgeBase, native dependencies và file còn sau khi thay container. Container/volume kiểm thử riêng được dọn sau khi chạy; đây không phải kiểm chứng nghiệp vụ SQL đầy đủ.

Lần build đầu cần mạng để lấy base images và NuGet packages. Hai base image mặc định đã được khóa bằng digest lấy từ registry trong lần build bước 2. Khi thay bản đã kiểm chứng, truyền image digest mới:

```text
--build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:<verified-sdk-digest>
--build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet@sha256:<verified-runtime-digest>
```

Không dùng ví dụ placeholder digest để build. Docker image .NET 8 không kéo dài vòng hỗ trợ của framework.

## 4. Chạy và kiểm tra container local

```powershell
docker run --detach --name saw-api-local --publish 127.0.0.1:8080:8080 --env-file .env.container.local --mount type=volume,source=saw-api-local-files,target=/app/App_Data saw-api:local
Invoke-RestMethod http://localhost:8080/health/live
Invoke-RestMethod http://localhost:8080/health/ready
```

Nếu tên `saw-api-local` đã tồn tại, kiểm tra đó là container của lần chạy trước rồi dừng/xóa đúng container đó trước khi tạo lại; không xóa volume chứa file cần giữ.

Kết quả mong đợi:

| Probe | HTTP | Ý nghĩa |
|---|---|---|
| `/health/live` | 200 | API process hoạt động; JSON có `status=Healthy` và `release` |
| `/health/ready` | 200 | SQL truy cập được, bảng lõi và migration yêu cầu đã có |
| `/health/ready` | 503 | Chưa có SQL/schema/migration hoặc DB đang lỗi; không coi API đã sẵn sàng |

Nếu hiện tại DB chưa được bootstrap/migration đúng, readiness 503 là kết quả dự kiến. Không thêm migration history giả chỉ để probe chuyển xanh. Probe này là kiểm tra triển khai tối thiểu, không thay thế test đầy đủ schema/trigger/nghiệp vụ.

Xem lỗi khởi động bằng `docker logs --tail 80 saw-api-local`. Không chia sẻ log chưa kiểm tra vì nghiệp vụ email local có thể chứa token. Swagger vẫn chỉ bật ở Development; container mặc định chạy Production nên `/swagger` không mở.

## 5. Upload và HTTPS

Ba loại file hiện tại dùng chung cây thư mục `/app/App_Data`: `SupplierFiles`, `DeliveryRefusalFiles`, `ContractFiles`. Named volume local giữ dữ liệu khi thay container; khi lên Azure cần mount Azure Files bền vững vào đúng `/app/App_Data` và kiểm chứng quyền ghi của user `app`.

Image chỉ listen HTTP nội bộ. `Http__RedirectToHttps=false` tránh redirect ở phía sau TLS termination. Trên Azure bắt buộc bật HTTPS Only tại App Service; nếu đặt container sau proxy khác, proxy đó cũng phải thực thi HTTPS. Chạy app trực tiếp ngoài Docker vẫn giữ HTTPS redirection mặc định.

Không có Docker `HEALTHCHECK` gọi curl vì runtime image không cài curl. Azure dùng HTTP health path `/health/ready`; local dùng HTTP probe như trên.

## 6. Tiêu chí hoàn thành

- [x] `docker version` có Server Linux trong lượt kiểm chứng.
- [x] Image `saw-api:step2` build thành công trên Linux amd64 và chạy bằng user `app` (UID 1654).
- [x] Build context loại cấu hình local/secret/upload; container xác nhận không có appsettings local.
- [x] `/health/live` trả 200; readiness trả 503 với SQL cố tình không truy cập được; release là `step2-workspace`.
- [x] KnowledgeBase và native dependencies SkiaSharp được kiểm tra trong container.
- [ ] Chạy test theo [Testing.md](Testing.md), kiểm thử upload/download và persistence ở bước tiếp theo.

Kết quả ngày 15/11/2026: **1.318 unit/HTTP tests passed, 0 failed, 0 skipped**; chạy lại riêng 4 health tests cũng pass. Smoke script xác nhận file marker còn sau khi thay container dùng cùng named volume. Chưa xác nhận readiness 200 với database thật, toàn bộ SQL/browser suite hoặc upload nghiệp vụ; các mục đó thuộc bước tiếp theo. TRX local nằm trong `test-results/container-preparation/`, bị Git bỏ qua.

Đã kiểm chứng smoke script trực tiếp bằng Windows PowerShell 5.1 sau khi bỏ các API chỉ có ở PowerShell 7; script init cũng đã kiểm tra tạo JWT key và giữ nguyên file cấu hình có sẵn bằng Windows PowerShell 5.1.

Sau bước này mới chuyển sang test SQL/container đầy đủ rồi chuẩn bị database Azure theo [kế hoạch triển khai](Azure-Docker-Deployment-Plan.md).

Nếu test runner tự khóa `SAW.Testing.exe` trên Windows, có thể chạy trực tiếp profile unit tương đương từ root repo:

```powershell
dotnet test SAW/SAW.sln --filter 'RequiresSql!=true' --logger 'trx;LogFileName=unit.trx' --results-directory test-results/container-preparation
```

Kiểm tra TRX có test thực chạy và không failed/skipped. Restore hiện có cảnh báo NU1903 cho AutoMapper 16.1.1, là dependency có sẵn của dự án; cần xử lý trước production. Bước Docker này chưa thay đổi phiên bản package đó.
