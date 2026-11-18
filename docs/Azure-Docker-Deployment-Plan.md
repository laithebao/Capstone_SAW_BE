# Kế hoạch deploy backend SAW lên Azure bằng Docker và thiết lập CI/CD

Ngày lập: **15/11/2026**. Phạm vi: repository `laithebao/Capstone_SAW_BE`.

## 1. Quyết định triển khai

- Giữ **.NET 8**, ASP.NET Core 8, EF Core 8 và SQL Server.
- Đóng gói API thành Linux container, chạy trên **Azure App Service for Linux**.
- Lưu image trong **Azure Container Registry (ACR) Basic**, tắt tài khoản admin của registry.
- Có **staging và production**; dùng hai Web App, hai App Service Plan B1 và hai database riêng.
- Chỉ triển khai backend. Frontend hiện tại chỉ liên quan đến CORS, URL email, Google login và URL QR.
- CI/CD dùng GitHub Actions. Một image đã kiểm thử được đưa lần lượt lên staging rồi production **theo digest**, không build lại khi promote.
- Tín dụng Azure Student **100 USD là tổng credit**, không phải 100 USD được cấp lại mỗi tháng.

Đây là **kế hoạch thực hiện**. Bước 2 đã được bổ sung Dockerfile, cấu hình local và health endpoints; xem [hướng dẫn Docker](Docker.md) để biết cách kiểm chứng. Workflow CI/CD và hạ tầng Azure chưa được tạo. Các lệnh deploy chỉ chạy sau khi hoàn thành bước chuẩn bị tương ứng. Chưa có tài nguyên Azure nào được tạo hoặc triển khai trong phiên làm việc này.

.NET 8 đã hết hỗ trợ ngày 10/11/2026. Docker cho phép đóng gói runtime nhưng không kéo dài thời gian hỗ trợ hay bảo đảm có bản vá .NET 8 mới. Quyết định giữ phiên bản này cần được theo dõi cùng lịch nâng cấp sau Capstone. [Chính sách .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## 2. Kiến trúc và giới hạn ngân sách

```text
GitHub Actions ── OIDC ──> Azure Container Registry
                                │ cùng image@sha256:...
                     ┌──────────┴───────────┐
                     v                      v
                API staging            API production
                App Service B1         App Service B1
                     │                      │
                 SQL staging            SQL production
                 Files staging          Files production
                 Secrets staging        Secrets production
```

| Thành phần | Cấu hình khởi đầu | Lý do / điều kiện |
|---|---|---|
| Region | Ưu tiên Southeast Asia | Xác nhận SKU, quota Student và giá trước khi tạo |
| API | 2 Linux B1 plan, mỗi plan 1 instance | Tách tài nguyên compute của staging và production |
| Database | 2 Azure SQL Database Basic | Chỉ dùng nếu dữ liệu dưới giới hạn 2 GB và test tải đạt; nâng S0 khi có bằng chứng cần |
| SQL logical server | Có thể dùng 1 server với 2 database | User và quyền theo từng database; cùng server vẫn có chung phạm vi quản trị |
| Registry | 1 ACR Basic | Dùng chung image, app identity chỉ có quyền pull |
| File nghiệp vụ | 2 storage account Standard LRS, mỗi môi trường 1 Azure Files share | File staging không dùng chung với production |
| Secret | Key Vault theo môi trường | App đọc qua managed identity và Key Vault references |
| Log | Console + Azure log/metrics | Đặt retention và giới hạn ingestion, tránh log toàn bộ payload |

Giá tham khảo công khai tại thời điểm khảo sát: Linux B1 khoảng 13,14 USD/tháng/plan và SQL Basic khoảng 4,90 USD/tháng/database. Hai plan cộng hai database khoảng **36 USD/tháng**, chưa gồm ACR, storage, log và network. Đây không phải báo giá đã chọn Southeast Asia hay giá đã xác nhận cho subscription của bạn. [Giá App Service](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/), [giá Azure SQL](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/).

**Cách quản lý 100 USD:**

1. Kiểm tra số credit thực còn lại và ngày hết hạn; không giả định còn đủ 100 USD.
2. Xuất dự toán theo region/offer bằng [Azure Pricing Calculator](https://azure.microsoft.com/en-us/pricing/calculator/). Tạm dự trù 45–60 USD/tháng cho cấu hình trên, phải thay bằng estimate thực tế trước khi tạo.
3. Nếu còn 100 USD, dành khoảng 20 USD dự phòng. 80 USD vận hành với estimate 45–60 USD/tháng tương đương khoảng **5–7 tuần**, không phải cả năm.
4. Đặt cảnh báo theo ngân sách thực chọn và kiểm tra credit hàng tuần. Budget alert không phải cơ chế tự ngắt chi tiêu.
5. Dừng Web App không làm App Service Plan hết tính phí. Nếu kết thúc sử dụng, xuất dữ liệu/backup rồi mới xóa tài nguyên đã xác nhận.
6. OpenAI API, Qdrant Cloud, Cloudinary và SMTP có cơ chế tính phí riêng. Credit Azure không tự thanh toán những tài khoản dịch vụ ngoài đang có trong code.

Để tiết kiệm hơn có thể cho hai app dùng chung một B1 plan, nhưng sẽ chia sẻ CPU/RAM. Đây là phương án giảm chi phí có đánh đổi, không phải cấu hình mặc định ở trên.

## 3. Các điểm đã xác nhận trong repository

| Phát hiện | Hệ quả cần giải quyết |
|---|---|
| `global.json` và 6 project đang dùng .NET 8 | Giữ nguyên target framework theo quyết định của bạn |
| Dockerfile đã được thêm ở bước 2; chưa có workflow CI/CD | Build/test image trước, tự động hóa phát hành ở bước 10 |
| `Program.cs` đã có health endpoint ở bước 2 | Liveness độc lập DB; readiness kiểm tra SQL, bảng lõi và migration yêu cầu |
| `InitialCreate.Up()` đang rỗng | Không thể coi EF migration hiện tại là quy trình tạo database trắng hoàn chỉnh |
| Có `SmartAgriWarehouseDB3.sql` và nhiều migration/trigger bổ sung | Phải đối chiếu schema, không import script rồi chạy toàn bộ migration một cách mù quáng |
| Supplier files, delivery refusal files, contract attachments đang ghi vào `App_Data` | Cần mount storage bền vững; không lưu file thật vào layer container |
| Contract attachments dùng `AppContext.BaseDirectory/App_Data/ContractFiles` | Mount toàn bộ `/app/App_Data` sẽ bao phủ cả đường dẫn này nếu image publish chạy tại `/app` |
| Có worker QR, nhắc giao hàng, lập chỉ mục KnowledgeBase | Bật Always On; tách dịch vụ ngoài theo môi trường; không scale out trước khi kiểm tra chống chạy trùng |
| Có test runner `tools/SAW.Testing` với profile unit/integration/browser/all | Tái sử dụng để CI không âm thầm bỏ qua SQL tests |
| Docker CLI đã có nhưng Docker Engine chưa chạy; chưa tìm thấy Azure CLI trong PATH | Cần mở Docker Desktop; thao tác Azure có thể dùng Cloud Shell trong Portal |

## 4. Lộ trình triển khai từng bước

### Bước 1 — Kiểm tra tài khoản, quota và thông tin cấu hình

Thực hiện trong Azure Portal:

1. Vào **Subscriptions**, chọn Azure for Students, ghi lại Subscription ID và Tenant ID.
2. Kiểm tra credit còn lại, hạn sử dụng, quyền tạo resource và role assignment.
3. Kiểm tra region có Linux B1, Azure SQL Basic và ACR Basic trong subscription. Nếu bị quota, chọn region/SKU khác sau khi tính lại chi phí.
4. Vào **Cost Management**, lập budget và cảnh báo; lưu estimate cấu hình đã chọn.
5. Kiểm tra gói GitHub và tính năng branch protection/environment cho repository. Không mặc định tài khoản Student đã có mọi tính năng của GitHub Enterprise.

Thông tin cần chuẩn bị: tên resource, region, origin frontend được phép, URL frontend cho email/QR, thông tin dịch vụ ngoài cho từng môi trường. Secret nhập trực tiếp vào Key Vault hoặc nơi cấu hình thích hợp, không gửi trong chat hay commit vào Git.

**Hoàn thành khi:** xác định được region/SKU khả dụng, số dư và thời gian có thể vận hành.

### Bước 2 — Chuẩn bị code để chạy trong container

Tạo các thành phần sau trong repo backend:

| Thành phần | Yêu cầu |
|---|---|
| `Dockerfile` | Multi-stage SDK 8 → ASP.NET 8; Linux amd64; publish Release; process chạy bằng user không phải root; listen `0.0.0.0:8080` |
| `.dockerignore` | Loại `.git`, `.vs`, `bin`, `obj`, dotenv, appsettings local, secret, DB dump, test artifacts và file upload |
| `.env.container.example` | Chỉ có tên biến và placeholder; file local chứa giá trị thật phải được gitignore |
| `/health/live` | Kiểm tra process; không gọi DB, OpenAI hay Cloudinary |
| `/health/ready` | Kiểm tra DB với timeout ngắn và điều kiện schema cần thiết; trả 503 khi chưa sẵn sàng |
| Thông tin release | Image mang commit SHA; smoke test xác nhận đang phục vụ đúng release |
| Cấu hình HTTPS | TLS kết thúc tại App Service; HTTPS Only bật ở Azure; tránh vòng lặp redirect ở HTTP nội bộ |
| Upload directory | Chuẩn hóa image chạy tại `/app`; tạo `/app/App_Data`, kiểm tra quyền ghi của user container với storage mount |

Lưu ý khi làm Dockerfile:

- Dùng image .NET 8 cuối cùng khả dụng đã kiểm chứng; ghi lại digest base image. Không dùng `latest` cho release.
- Restore trước, copy source sau để tận dụng cache; build context là root repo backend.
- Không copy `appsettings.json` đang chứa cấu hình local; toàn bộ cấu hình production đến từ runtime environment/Key Vault.
- Giữ KnowledgeBase được copy vào publish output theo project hiện tại.
- Kiểm tra thư viện native của SkiaSharp trên Linux bằng tác vụ sinh QR thật.
- Không nhúng certificate, JWT key, OpenAI key, connection string hoặc registry credential trong image/build argument.
- Không chạy migration tự động mỗi lần API khởi động.

**Hoàn thành khi:** container khởi động được bằng cấu hình environment, có health endpoint và không chứa secret local.

### Bước 3 — Build và test Docker trên máy

Mở Docker Desktop ở chế độ Linux containers. Tại root repo backend:

```powershell
docker version
dotnet run --project tools/SAW.Testing -- test --profile unit
dotnet run --project tools/SAW.Testing -- setup --docker --profile integration
dotnet run --project tools/SAW.Testing -- test --profile integration
docker build --platform linux/amd64 -t saw-api:local .
```

Sau khi tạo `.env.container.local` từ file mẫu và cấu hình SQL local, chạy:

```powershell
docker run --detach --name saw-api-local --publish 127.0.0.1:8080:8080 --env-file .env.container.local --mount type=volume,source=saw-api-local-files,target=/app/App_Data saw-api:local
Invoke-RestMethod http://localhost:8080/health/live
Invoke-RestMethod http://localhost:8080/health/ready
```

SQL trên máy host phải dùng địa chỉ container truy cập được, ví dụ `host.docker.internal` trên Docker Desktop; `localhost` bên trong API container chỉ đến chính container đó. SQL test trong `compose.test.yml` đang bind loopback: nếu dùng làm SQL cho container app, cần cấu hình mạng chung hoặc đường kết nối riêng đã kiểm chứng, không tự mở SQL ra Internet.

Kiểm chứng thêm: upload/download file, restart rồi đọc lại file, sinh QR, đọc KnowledgeBase, xử lý request có JWT. Không dùng database test tự tạo của fixture làm database app lâu dài vì fixture sẽ dọn database của nó.

**Hoàn thành khi:** test đã chọn đều chạy và pass, container pass smoke test, dữ liệu file còn sau khi thay container sử dụng cùng volume.

### Bước 4 — Chuẩn bị quy trình database trước khi tạo production

Đây là bước có thể tốn thời gian nhất của dự án hiện tại:

1. **Phương án đã chốt: chuyển database hiện có `SmartAgriWarehouseDB` lên Azure SQL**, giữ dữ liệu nghiệp vụ, tài khoản ứng dụng, role và 28 dòng migration history hiện có. Dừng nhánh tạo schema mới rồi seed/baseline thủ công; giữ nguyên database thử `SAW_AzureSchema_Check` để tham khảo.
2. Dừng API/worker và các thao tác ghi trong lúc lấy bản sao. Tạo full backup dạng copy-only bằng SSMS, kiểm tra backup đọc được rồi restore thành database local riêng, ví dụ `SAW_AzureMigration`. File `.bak` phục vụ bảo vệ nguồn và tạo bản sao local; không restore trực tiếp `.bak` vào Azure SQL Database.
3. **Phương án hiện tại: giữ toàn bộ bảng và dữ liệu, ngoại trừ ba view cũ đã được người dùng chọn xóa để xử lý SQL71501:** `VW_BATCH_TRACEABILITY`, `VW_PUBLIC_BATCH_TRACEABILITY`, `VW_PUBLIC_BATCH_TRANSACTION_HISTORY`. Đã kiểm tra code không gọi các view này và không tìm thấy dependency/module SQL tham chiếu chúng trong database nguồn tại thời điểm kiểm tra. Người dùng được hướng dẫn xóa trực tiếp trên `SmartAgriWarehouseDB` và đã báo thành công; không coi database nguồn còn nguyên ba view. Backup trước thay đổi đã qua `RESTORE VERIFYONLY` khi cung cấp đủ hai media files. Không seed hoặc ghi migration history thủ công.
4. Dùng SSMS **Export Data-tier Application**, chọn toàn bộ bảng, bao gồm `__EFMigrationsHistory`. File export hiện có tại `docs/SmartAgriWarehouseDB.bacpac`; người dùng đã báo thành công. Tiếp theo tạo Azure SQL logical server và import qua SSMS. Nếu còn lỗi validation/tương thích, lưu nguyên thông báo để thống nhất cách xử lý; không tự sửa hoặc xóa thêm đối tượng hay bỏ kiểm tra. BACPAC chứa schema và dữ liệu; bảo quản như dữ liệu nhạy cảm, không commit vào Git. Chỉ xác nhận chuyển thành công sau khi import và đối soát thực tế đạt.
5. Tạo Azure SQL logical server và import BACPAC vào database mới hoặc trống. Chọn SKU theo dung lượng, tải và credit còn lại. SQL login cấp server ở máy local không tự trở thành tài khoản kết nối Azure; tạo tài khoản runtime Azure riêng với quyền phù hợp.
6. Đối soát bản import với bản sao nguồn: số bản ghi từng bảng, role/tài khoản ứng dụng, toàn bộ 28 migration ID và ProductVersion, constraint/index/trigger/view, tồn kho và các quan hệ nghiệp vụ. Không chèn lại seed hoặc migration history đã có trong BACPAC. Không dùng database local đang phát sinh dữ liệu mới làm mốc đối soát cho bản sao cũ.
7. Chuyển cấu hình API staging sang database Azure và kiểm tra readiness, login, quyền truy cập cùng các luồng nghiệp vụ. File trong `/app/App_Data` không nằm trong BACPAC; cần chuyển storage riêng. Trước production, xác nhận dữ liệu/tài khoản được phép sử dụng, bỏ credential thử nghiệm và diễn tập chuyển dữ liệu lần cuối có thời điểm dừng ghi rõ ràng.

Tài khoản chạy migration có quyền DDL riêng. API runtime chỉ có các quyền đọc/ghi và execute cần thiết. Không chạy test fixture cần `CREATE/DROP DATABASE` trên Azure SQL production.

**Hoàn thành khi:** một Azure SQL database mới được dựng đầy đủ bằng quy trình đã version hóa và test đạt. Chưa đạt bước này thì chưa đưa production vào sử dụng.

### Bước 5 — Tạo resource group, ACR và đẩy image đầu tiên

Dùng **Azure Portal → Cloud Shell → PowerShell** để không cần cài Azure CLI trên máy. Ví dụ tên dưới đây cần đổi theo subscription của bạn; ACR và Web App phải có tên duy nhất.

```powershell
$subscriptionId = '<subscription-id>'
$region = 'southeastasia'
$acrName = '<ten-acr-duy-nhat-chi-chu-va-so>'
az account set --subscription $subscriptionId
az group create --name rg-saw-shared --location $region
az group create --name rg-saw-staging --location $region
az group create --name rg-saw-production --location $region
az acr create --resource-group rg-saw-shared --name $acrName --sku Basic --admin-enabled false
```

Trên máy có Docker Engine và Azure CLI đã đăng nhập đúng subscription:

```powershell
az acr login --name $acrName
$acrServer = az acr show --name $acrName --query loginServer --output tsv
$commit = git rev-parse HEAD
docker tag saw-api:local "${acrServer}/saw-api:${commit}"
docker push "${acrServer}/saw-api:${commit}"
az acr repository show --name $acrName --image "saw-api:${commit}" --query digest --output tsv
```

Chỉ tag image vừa build từ đúng commit đang ghi nhận; nếu code thay đổi sau build thì build lại trước khi tag. Lưu release bằng `registry/saw-api@sha256:...`. Cloud Shell không mặc nhiên có Docker daemon để chạy lệnh build/push; có thể dùng GitHub Actions sau bước 10 thay cho thao tác local này.

**Hoàn thành khi:** ACR có image kiểm thử được và đã ghi lại commit + image digest.

### Bước 6 — Tạo staging App Service, database và storage

Trong resource group staging:

1. Tạo **App Service Plan Linux B1** và một Web App chạy custom Docker container từ ACR. Dùng image digest đã ghi ở bước 5.
2. Bật managed identity cho app. Cấp identity quyền `AcrPull` trên ACR với mô hình registry RBAC truyền thống; nếu registry bật ABAC thì dùng role repository tương ứng.
3. Cấu hình App Service dùng managed identity để pull ACR; giữ ACR admin account tắt.
4. Cấu hình container target port **8080**. Với single-container classic dùng `WEBSITES_PORT=8080`; nếu tạo app theo mô hình sidecar thì đặt target port của main container là 8080. Không giả định biến classic điều khiển cổng ở sidecar.
5. Bật **HTTPS Only**, minimum TLS phù hợp và **Always On**. Đặt health-check path `/health/ready` sau khi endpoint đã tồn tại.
6. Tạo database staging Azure SQL Basic và thiết lập Entra admin. Cấp contained user cho app identity vào đúng database; thử kết nối bằng `Authentication=Active Directory Managed Identity` với SqlClient hiện tại.
7. Mở SQL firewall cho outbound IP cần thiết của staging app, hoặc dùng VNet/private connectivity nếu ngân sách cho phép. Không bật quyền truy cập rộng cho mọi Azure service làm cấu hình mặc định.
8. Tạo storage account Standard LRS và Azure Files share riêng. Mount share vào **`/app/App_Data`** trong app. Kiểm tra quyền ghi với user không phải root; không đưa app vào sử dụng nếu mount lỗi.
9. Tạo Key Vault staging, cấp app identity quyền đọc secret. Cấu hình app settings tham chiếu Key Vault.
10. Thực hiện bootstrap/migration/seed đã kiểm chứng ở bước 4. Nếu cần truy cập SQL từ Cloud Shell/máy quản trị, chỉ mở firewall cần thiết trong thời gian thực hiện rồi gỡ rule tạm.

Ban đầu app có thể chưa khởi động khi quyền pull image/configuration/DB chưa xong. Chỉ coi bước này hoàn tất sau khi health check và nghiệp vụ đều đạt.

### Bước 7 — Nhập runtime configuration

| Tên app setting | Giá trị / cách quản lý |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Staging` hoặc `Production` |
| `ASPNETCORE_HTTP_PORTS` | `8080` |
| `ConnectionStrings__DefaultConnection` | Azure SQL connection string theo môi trường, TLS bật; ưu tiên managed identity |
| `Cors__AllowedOrigins__0` | Origin frontend chính xác, gồm scheme; không có đường dẫn hoặc dấu `/` cuối |
| `Jwt__Key` | Secret ngẫu nhiên đủ mạnh, tối thiểu 32 byte theo code hiện tại |
| `Jwt__Issuer`, `Jwt__Audience` | Giá trị ổn định riêng cho từng môi trường |
| `Email__DeliveryMode` | `Log` chỉ cho test có kiểm soát; môi trường sử dụng thật dùng SMTP và không ghi token email vào log |
| `Smtp__Host`, `Smtp__Port`, `Smtp__EnableSsl` | Thông số nhà cung cấp SMTP |
| `Smtp__Username`, `Smtp__Password` | Secret khi nhà cung cấp yêu cầu |
| `Smtp__FromEmail`, `Smtp__FromName` | Người gửi đã xác minh |
| `Smtp__EmailVerificationUrl`, `Smtp__PasswordResetUrl` | Trang frontend đúng môi trường |
| `GoogleAuth__ClientId` | Khớp OAuth client frontend; cấu hình origin Google tương ứng |
| `Cloudinary__CloudName`, `Cloudinary__ApiKey`, `Cloudinary__ApiSecret` | Cấu hình backend riêng hoặc giới hạn namespace/quota môi trường |
| `QrCode__PublicFrontendBaseUrl` | URL frontend thật để QR không trỏ về localhost |
| `QrCode__Enabled` | Bật sau khi Cloudinary và frontend URL sẵn sàng |
| `OpenAI__ApiKey` | Key riêng theo môi trường, giới hạn chi phí ở nhà cung cấp |
| `Qdrant__Endpoint`, `Qdrant__ApiKey`, `Qdrant__CollectionAlias` | Endpoint HTTPS; collection staging và production tách biệt |
| `AiChat__KnowledgeBaseAutoSync` | Quyết định rõ cách khởi tạo/cập nhật index; không dùng staging ghi vào collection production |

Các setting hỗ trợ HTTPS termination/release metadata chỉ được nhập sau khi code tương ứng ở bước 2 được triển khai. Không thêm tên biến chưa được code đọc rồi coi là đã bật chức năng.

Nếu chưa dùng được managed identity cho SQL, dùng SQL user riêng theo môi trường với quyền tối thiểu và secret trong Key Vault; không dùng SQL admin làm tài khoản API.

### Bước 8 — Kiểm thử staging và lưu bằng chứng

- `/health/live` trả 200; `/health/ready` trả 200 với đúng database/schema.
- Response/metadata xác nhận đúng commit/image đang triển khai, tránh kiểm tra nhầm container cũ.
- Login, refresh token, role authorization và CORS đạt. Origin ngoài allowlist không đọc được API qua browser.
- Chạy một luồng nhập kho → QC → picking → xuất kho và một luồng điều chỉnh tồn, kiểm tra audit/trigger/concurrency tương ứng.
- Upload, download, restart app, triển khai lại image rồi tải lại file cũ; xác nhận storage mount không bị thay bằng filesystem tạm.
- Kiểm tra QR, email verification/reset password, AI/RAG theo tính năng thực tế sẽ bật.
- Dùng dữ liệu staging để thử tải ban đầu, ví dụ 10 người dùng đồng thời trong 10 phút. Mục tiêu ban đầu: không có lỗi 5xx không giải thích được ở các API nghiệp vụ; ghi nhận p95 và memory/CPU/DTU để quyết định B1/Basic có đủ hay không. Tách AI request khỏi mục tiêu latency của CRUD.
- Mô phỏng lỗi kết nối DB: readiness về 503, log có ích và không lộ credential.

Lưu commit, digest, cấu hình môi trường không chứa secret, kết quả test và các giới hạn đã biết vào release record. Không coi HTTP 200 của `/` là đủ để xác nhận hệ thống chạy đúng.

### Bước 9 — Tạo production và phát hành lần đầu

1. Tạo bộ tài nguyên production tương tự staging, với app/plan/database/storage/secret riêng.
2. Kiểm tra credit và forecast một lần nữa trước khi bật cả hai môi trường chạy liên tục.
3. Khởi tạo production database bằng script đã rehearsal. Chỉ seed dữ liệu cần thiết.
4. Cấu hình backup/PITR theo giới hạn SKU và backup Azure Files. Thử restore vào tài nguyên khác trước khi nhận dữ liệu quan trọng; thời gian khôi phục mục tiêu phải dựa trên lần thử thực tế.
5. Deploy **đúng image digest đã pass staging**. Không rebuild, không dùng tag `latest`.
6. Chạy smoke test production bằng tài khoản/dữ liệu kiểm thử được kiểm soát; không chạy suite tạo/xóa DB hoặc seed demo lên production.
7. Kết nối frontend với URL API production; kiểm tra lại CORS, Google, QR và email URL.

Mỗi app B1 chỉ có một instance. Kế hoạch này có staging/production và rollback nhưng chưa bảo đảm HA hoặc zero-downtime. Deployment slots cần App Service Standard trở lên; chưa đưa chi phí này vào cấu hình Student mặc định. [Deployment slots](https://learn.microsoft.com/en-us/azure/app-service/deploy-staging-slots).

### Bước 10 — Tự động hóa CI/CD sau khi deploy thủ công đạt

Đã chuẩn bị workflow trong **repo backend**, cho một Web App hiện có `saw-api`:

| File hiện tại | Trigger | Trách nhiệm |
|---|---|---|
| `.github/workflows/backend-ci-cd.yml` | PR vào `main`, push `main`, hoặc manual trên `main` | Job CI không có quyền OIDC: unit + SQL integration, build một image, smoke. Job deploy riêng chỉ chạy cho `main` của repo chính sau CI: tải artifact đúng run, OIDC, push ACR, cập nhật sidecar `main` bằng digest, kiểm tra health đúng commit và thử rollback nếu thất bại. |

Hướng dẫn cấu hình và vận hành: [GitHub Actions cho Azure](GitHub-Actions-Azure.md).
Deployment được tuần tự hóa; commit đã bị thay thế trên `main` sẽ không cập nhật
Web App. Bản cập nhật migration sinh SQL trong CI, chờ duyệt tại GitHub
Environment `azure-release`, chạy đúng SQL đã duyệt lên Azure SQL rồi mới deploy
API. Không còn sửa tay `APPROVED_MIGRATION_HEAD`. Mốc import trong
`scripts/ci/database-baseline.txt` là lịch sử cố định, không cập nhật mỗi release.
Pipeline deploy API trước đây đã chạy thành công; luồng migration có phê duyệt
mới cần cấu hình environment, federated credential, SQL secret và quyền firewall
theo tài liệu liên kết, rồi kiểm chứng trên GitHub trước khi coi đã vận hành.

Khi triển khai production thành môi trường riêng, bổ sung bước promote image
digest đã pass staging và danh tính/quyền production riêng theo bước 9; cấu hình
hiện tại chưa tạo môi trường production thứ hai. Rollback thủ công dùng digest
trong deployment record, còn rollback khi phát hành lỗi được xử lý trong job deploy.

Chi tiết bắt buộc khi triển khai workflow:

1. **CI không có production credentials.** PR chỉ kiểm thử; không dùng `pull_request_target` để chạy code PR có quyền deploy.
2. Dùng runner Linux x64. SQL Server 2022 container chỉ dùng cho test, không mang SQL Developer image lên production.
3. Dùng `tools/SAW.Testing --profile integration` để bao gồm test unit và SQL hiện có. Kiểm tra TRX có test thực chạy, không có failure/skipped trong profile đã chọn. Browser/external là gate bổ sung theo luồng phát hành, không giả vờ đã chạy khi không đủ dependency.
4. Build image **một lần**. Chuyển image đã test giữa job bằng OCI/image artifact hoặc push từ chính job đã test; không dựng lại từ branch mới nhất trong job deploy.
5. Push ACR bằng OIDC identity có quyền push riêng. App runtime dùng identity chỉ có quyền pull. Tách quyền deploy staging/production; provisioning/role assignment là quyền bootstrap riêng.
6. Với workflow phụ thuộc `workflow_run`, xác minh workflow nguồn, repository nguồn, event `push`, branch `main`, commit SHA, kết quả thành công và artifact thuộc đúng run. Không thực thi script tùy ý lấy từ artifact.
7. Release manifest chứa commit SHA, image digest, CI run ID, thời điểm/staging result và migration head yêu cầu. Production nhận release đã xác minh, không nhận chuỗi image tùy ý rồi deploy.
8. Nếu có thay đổi schema, áp dụng migration đã review trước bằng danh tính triển khai DB riêng và kiểm tra schema tương thích. Không nhúng DDL credential vào API; không giấu thao tác migration trong startup.
9. Nếu GitHub plan hỗ trợ, dùng environment protection và required reviewer. Repo private có giới hạn theo gói; manual dispatch không tương đương phê duyệt độc lập của người thứ hai. Kiểm tra quyền người có thể phát hành trước khi coi gate đã được bảo vệ. [GitHub environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments).
10. Serialize deployment theo app/environment. Chỉ cancel CI cũ khi phù hợp; không hủy job production giữa migration/deploy.
11. Pin action theo commit đã kiểm chứng, dùng quyền `GITHUB_TOKEN` tối thiểu, đặt timeout và retention artifact. Giữ image các release cần rollback; không dọn nhầm digest production đang chạy.
12. Nếu staging thất bại, không cho promote. Nếu smoke production thất bại, quay về image trước khi việc đó tương thích với schema; vẫn đánh dấu release thất bại và lưu bằng chứng.

Trong lần cấu hình đầu, dùng Azure CLI/Portal bootstrap OIDC federation cho đúng repository và branch/environment. Client ID, Tenant ID và Subscription ID không phải mật khẩu; app secret, JWT key, SQL password và API key phải nằm đúng kho secret. [App Service container CI/CD](https://learn.microsoft.com/en-us/azure/app-service/deploy-container-github-action).

### Bước 11 — Rollback và thay đổi database an toàn

1. Trước mỗi deploy, ghi lại image digest hiện tại, schema/migration head và cấu hình đã thay đổi.
2. Khi release mới lỗi, xác định lỗi do image, config, DB hay dịch vụ ngoài.
3. Với lỗi image và schema còn tương thích: cấu hình lại App Service về digest trước, chờ đúng phiên bản được phục vụ, chạy smoke và luồng nghiệp vụ đại diện.
4. Với thay đổi config: khôi phục cấu hình trước có kiểm soát. Image rollback không tự khôi phục app settings/Key Vault secret.
5. Với migration phá tương thích hoặc sai dữ liệu: dừng thao tác ghi theo runbook sự cố, chọn forward-fix hoặc restore vào DB mới và đối soát trước khi chuyển kết nối. Không tự `database update` về migration cũ hay restore đè khi người dùng đã phát sinh dữ liệu mới.
6. Ưu tiên migration mở rộng trước, cập nhật app sau, rồi mới thu hẹp schema ở release khác. Kiểm tra cả image cũ và mới chạy được trên schema mở rộng trước khi phát hành.

**Hoàn thành khi:** đã diễn tập rollback image trên staging và thử restore dữ liệu/file, có kết quả và thời gian thực tế.

### Bước 12 — Vận hành sau phát hành

- Theo dõi availability, 5xx, latency, restart, memory/CPU, SQL DTU/storage, lỗi worker và chi phí.
- Bật cảnh báo vừa đủ cho downtime/lỗi kéo dài và credit; kiểm tra cảnh báo đến đúng người nhận.
- Kiểm tra file mount, backup, dung lượng và quyền truy cập định kỳ.
- Không scale API nhiều instance trước khi kiểm chứng worker QR/nhắc giao hàng/indexing có idempotency hoặc distributed lock phù hợp.
- Chốt người phụ trách phát hành, người xử lý sự cố và lịch kết thúc tài nguyên trước khi hết credit.
- Theo dõi rủi ro .NET 8 và lập lịch chuyển runtime được hỗ trợ sau giai đoạn đã thống nhất.

## 5. Thứ tự thực hiện và tiêu chí kết thúc

| Chặng | Công việc | Bằng chứng cần có |
|---|---|---|
| A | Tài khoản, ngân sách, Docker/runtime preparation | Estimate + container chạy được |
| B | Bootstrap DB và storage | Dựng DB mới thành công; file còn sau redeploy |
| C | Deploy staging thủ công | Health, auth, nghiệp vụ và dịch vụ ngoài đạt |
| D | Deploy production thủ công | Cùng image digest, cấu hình riêng, backup/restore thử đạt |
| E | Tự động hóa GitHub Actions | PR fail không deploy; main lên staging; production promote đúng release |
| F | Diễn tập rollback và bàn giao | Quay lại image trước, kiểm chứng dữ liệu và runbook vận hành |

Ước lượng lập lịch ban đầu: khoảng **3–5 ngày làm việc** nếu DB/bootstrap thuận lợi; cần điều chỉnh sau bước 4 và theo quota/tài khoản thực tế. Đây là ước lượng công việc, chưa phải kết quả đã triển khai.

Checklist nghiệm thu:

- [ ] Chỉ BE được triển khai; target vẫn `net8.0`.
- [ ] Container không chứa secret, chạy non-root, đúng cổng và có health checks.
- [ ] Staging và production tách database, file và secret.
- [ ] DB được dựng từ quy trình có version, không phụ thuộc DB local của một thành viên.
- [ ] File upload còn sau restart/redeploy; backup và restore đã thử.
- [ ] CI kiểm thử SQL thực và container thực, có artifact bằng chứng.
- [ ] Production dùng đúng digest đã pass staging, deployment được kiểm soát quyền.
- [ ] Rollback đã diễn tập; biết rõ giới hạn khi schema/data thay đổi.
- [ ] Chi phí thực phù hợp số credit còn lại và thời gian cần sử dụng.

## 6. Tài liệu đối chiếu

- [Cấu hình custom container, ACR identity, cổng và persistent storage](https://learn.microsoft.com/en-us/azure/app-service/configure-custom-container).
- [GitHub Actions triển khai container lên App Service](https://learn.microsoft.com/en-us/azure/app-service/deploy-container-github-action).
- [Key Vault references cho App Service](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references).
- [App Service health check](https://learn.microsoft.com/en-us/azure/app-service/monitor-instances-health-check).
- [Áp dụng EF Core migration](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).
- [Azure for Students và credit](https://azure.microsoft.com/en-us/pricing/offers/ms-azr-0170p/).
