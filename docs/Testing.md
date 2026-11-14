# Test SAW từ đầu trên Windows, macOS và Linux

Hướng dẫn cho mọi thành viên sau khi clone/pull GitHub. Chạy các lệnh từ thư mục
backend trừ khi ghi rõ. Cú pháp `dotnet run` giống nhau trên PowerShell, CMD, bash và
zsh; không cần Python, `source`, `export` hay PowerShell 7.
Lần thiết lập đầu cần Internet để tải SDK/package NuGet/npm và image Docker.

## 1. Cài công cụ

| Công cụ | Khi nào cần | Kiểm tra |
|---|---|---|
| [Git](https://git-scm.com/downloads) | Clone/pull | `git --version` |
| [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) | Mọi profile; cài SDK, không chỉ Runtime | `dotnet --list-sdks` |
| [Node 22 LTS](https://nodejs.org/download/release/latest-v22.x/) + npm | Browser/full; **>=22.22.0 và <23** | `node --version` và `npm --version` |
| Docker Desktop hoặc Engine + Compose v2 | Tự tạo SQL test | `docker version` và `docker compose version` |
| Chrome hoặc Edge | Browser/full | Cài browser Chromium bản thường |

Mở Terminal mới sau khi cài để cập nhật PATH. `global.json` chọn SDK 8.x, FE có
`.node-version` chọn Node 22. Node 24 không thỏa package.json của FE nên runner sẽ
yêu cầu đổi phiên bản trước khi chạy browser.

- **Windows:** Docker Desktop dùng **Linux containers**, thường qua WSL2.
- **macOS Intel:** Docker Desktop chạy image SQL x64; Chrome/Edge trong `/Applications`.
- **Linux:** Docker Engine + Compose plugin, Chrome/Chromium/Edge trong PATH. Native
  Skia Linux đã được khai báo trong backend.
- **Mac Apple Silicon / Windows ARM / Linux ARM:** SQL Server image dùng x64.
  Có thể dùng SQL Server trên host x64 từ xa bằng `--existing`. Docker có thể chạy
  giả lập x64 trên Mac ARM, nhưng Microsoft không hỗ trợ chính thức cấu hình giả lập.
  Không thể bảo đảm SQL giả lập ổn định trên mọi CPU chỉ bằng script.

Tham khảo [chính sách SQL Server container](https://learn.microsoft.com/en-us/sql/linux/containers/deploy?view=sql-server-ver17).
Đường kết nối SQL x64 từ xa dùng được từ cả ba OS.

## 2. Clone hai repo cạnh nhau

```text
mkdir SAWWorkspace
cd SAWWorkspace
git clone https://github.com/laithebao/Capstone_SAW_BE.git Capstone_SAW_BE
git clone https://github.com/laithebao/Capstone_SAW_FE.git Capstone_SAW_FE
cd Capstone_SAW_BE
```

Repo private cần đăng nhập GitHub bằng Credential Manager hoặc SSH của thành viên.

```text
SAWWorkspace/
  Capstone_SAW_BE/SAW/SAW.sln
  Capstone_SAW_BE/tools/SAW.Testing/
  Capstone_SAW_FE/package.json
  Capstone_SAW_FE/package-lock.json
```

Frontend cần đúng tên thư mục `Capstone_SAW_FE`. Chỉ chạy unit/integration thì không
cần FE. Hai repo phải ở commit/nhánh tương thích; PR thay đổi API nên ghi commit FE
đã kiểm chứng. Pull backend không tự cập nhật frontend.

## 3. Chạy ngay test không cần database

```text
dotnet run --project tools/SAW.Testing -- test --profile unit
```

Lệnh restore/build và chạy unit, InMemory, HTTP test với repository mock. Các test
SQL/browser/Cloudinary được loại bằng filter, không âm thầm skipped. Không cần Docker,
frontend, file local hay tài khoản dịch vụ ngoài cho profile này.

## 4. Tạo cấu hình local

```text
dotnet run --project tools/SAW.Testing -- init
```

Lệnh tạo `.env.test.local` từ `.env.test.example`, sinh mật khẩu SQL ngẫu nhiên,
mặc định dùng `127.0.0.1:1434`, `Database=master`. File Unix có quyền 600. Nếu đã có
file, init giữ nguyên. File mẫu được commit; file local và secret bị Git bỏ qua.

Nếu cổng 1434 bận, chọn cổng khác khi tạo file lần đầu:

```text
dotnet run --project tools/SAW.Testing -- init --port 1445
```

Nếu file đã tồn tại, sửa port trong `SAW_TEST_SQL_CONNECTION` bằng editor. Runner
không tự dừng/xóa container khác đang chiếm cổng.

Nội dung file có dạng:

```dotenv
SAW_TEST_SQL_CONNECTION='Server=127.0.0.1,1434;Database=master;User Id=sa;Password=<init tự sinh>;Encrypt=False;TrustServerCertificate=True;Connect Timeout=30'
EDGE_PATH=''
SAW_TEST_CLOUD_NAME=''
SAW_TEST_CLOUD_KEY=''
SAW_TEST_CLOUD_SECRET=''
```

Giữ mật khẩu init đã sinh; không chép placeholder trong ví dụ đè vào file. Để trống
EDGE_PATH cho runner tự tìm browser. Nếu không tìm thấy, điền executable:

| OS | Ví dụ EDGE_PATH |
|---|---|
| Windows | `C:\Program Files\Google\Chrome\Application\chrome.exe` |
| macOS | `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome` |
| Linux | `/usr/bin/chromium` hoặc `/usr/bin/google-chrome` |

Giữ dấu cách/backslash trong cặp nháy, không thêm escape kiểu JSON. File được đọc như
dữ liệu, không chạy bằng shell: không dùng `$VAR`, `${VAR}` hay command substitution.
Giá trị local khác rỗng ưu tiên hơn environment; ô trống cho phép lấy secret/EDGE_PATH
từ environment CI. Mọi lệnh hỗ trợ `--env-file <path>` để dùng cấu hình khác.
Dùng tên `.env.test.<tên>` hoặc đặt file ngoài repo để cấu hình khác cũng được giữ
ngoài Git; không commit một file chứa secret chỉ vì nó có tên khác `.env.test.local`.

## 5. Tạo SQL và database test

### Cách A: SQL test mới bằng Docker

Mở Docker Desktop/chạy Engine, chờ Docker sẵn sàng:

```text
dotnet run --project tools/SAW.Testing -- setup --docker --profile browser
```

Lệnh thực hiện:

1. Tạo/start SQL Server 2022 Developer theo `compose.test.yml`: image pin digest,
   volume riêng, cổng local theo cấu hình, tối đa 4 CPU theo số CPU Docker hiện có,
   giới hạn 4 GB RAM và healthcheck. Cấp đủ RAM cho Docker hoặc dùng SQL có sẵn.
2. Chờ SQL sẵn sàng; mật khẩu được truyền qua environment, không in ra console.
3. **Tạo database `SAW_Setup_Test_<GUID>`**, tạo bảng, insert/read dữ liệu thử rồi xóa
   database. Bước này kiểm chứng quyền tạo/schema/đọc/ghi/xóa trước khi chạy suite.
4. Kiểm tra Node 22/Chrome, chạy `npm ci` tại FE theo package-lock.json.

Không cần cài SSMS/sqlcmd trên host, nạp DB của người khác, chạy API riêng hoặc Vite
riêng. Nếu chỉ cần SQL/API test, bỏ dependency browser bằng:

```text
dotnet run --project tools/SAW.Testing -- setup --docker --profile integration
```

### Cách B: SQL test có sẵn hoặc trên host x64 khác

Sửa biến chung trong `.env.test.local`, không tạo thêm biến riêng cho Supplier:

```dotenv
SAW_TEST_SQL_CONNECTION='Server=<host>,1433;Database=master;User Id=<test-user>;Password=<test-password>;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30'
```

Tài khoản cần quyền tạo database và làm chủ database vừa tạo, ví dụ dbcreator trên
SQL Server dành cho test. Cấu hình TLS/certificate và firewall theo server thực tế.
Mẫu Encrypt=False/TrustServerCertificate=True chỉ dành cho môi trường local tương ứng.

```text
dotnet run --project tools/SAW.Testing -- setup --existing --profile browser
```

Có thể thay browser bằng integration. Lệnh vẫn tạo/xóa database probe để kiểm tra
quyền, nhưng không start Docker. Máy đang dùng `saw-test-sql2022` trên cổng 1434 có
thể giữ nguyên và chọn `--existing`; không start Compose mới trên cổng đó.

### Database khi chạy từng ca

Master chỉ dùng cho CREATE/DROP, không ghi dữ liệu nghiệp vụ test vào master:

| Fixture | Database tự tạo | Chuẩn bị |
|---|---|---|
| Receiving/workflow | `SAW_UC28_Test_<GUID>` | EF baseline/model, migration và trigger đã version |
| Supplier | `SAW_<GUID>_SupplierTests` | EF schema và dữ liệu từng ca |
| GoodsIssue migration | `SAW_GoodsIssueMigration_Test_<GUID>` | Baseline và migration thật cần kiểm tra |

Fixture tự seed role, account, supplier, hợp đồng, lô hàng, tồn kho… gọi service/API,
kiểm tra dữ liệu thật rồi dọn database sở hữu. Một số ca cùng fixture dùng chung DB
nhưng tách entity theo ID/GUID; Supplier cô lập theo từng ca để bộ đếm có trạng thái
xác định. Các tác vụ concurrency trong test vẫn giữ nguyên.

**Một SQL Server trống là đủ.** Trigger nhập kho/soạn hàng/xuất kho/điều chỉnh tồn
nằm trong Git và được nhúng vào project test. Không cần import file SQL ngoài repo
hoặc đọc trigger từ database development. Khi thay schema nghiệp vụ, review resource
test tương ứng; migration test vẫn chạy chuỗi migration thật trên baseline cô lập.

## 6. Chạy các mức test

```text
dotnet run --project tools/SAW.Testing -- test --profile integration
dotnet run --project tools/SAW.Testing -- test --profile browser
```

| Profile | Tập được chọn | Cần |
|---|---|---|
| unit | Unit/InMemory/HTTP mock | SDK |
| integration | Unit + SQL/API, migration, transaction, lock | SQL test |
| browser | Integration + frontend thật | SQL, FE, Node 22, Chrome/Edge |
| all | Tất cả, gồm upload/download Cloudinary thật | Thêm Cloudinary test credentials |

Muốn chạy **tất cả**, điền `SAW_TEST_CLOUD_NAME`, `SAW_TEST_CLOUD_KEY`,
`SAW_TEST_CLOUD_SECRET` từ tài khoản Cloudinary test vào file local rồi chạy:

```text
dotnet run --project tools/SAW.Testing -- test --profile all
```

Có thể dùng environment/CI secrets nếu các ô trong file để trống. Runner tự bật
SAW_TEST_BROWSER/SAW_TEST_REAL_CLOUDINARY theo profile. All thiếu secret sẽ dừng sớm;
browser không chọn hai ca Cloudinary thật và không được coi là đã kiểm chứng dịch vụ
này. Cloudinary cần Internet/quota; test xóa đúng asset GUID do nó tạo.

Nhóm AI dùng mock/stub model/HTTP OpenAI-Qdrant; các ca SQL AI vẫn dùng SQL thật.
Không cần API key AI thật và không nghiệm thu chất lượng AI thật trong suite này.

Chạy riêng Supplier hai lần để kiểm tra tính lặp:

```text
dotnet run --project tools/SAW.Testing -- test --profile integration --filter "FullyQualifiedName~SupplierSqlIntegrationTests"
dotnet run --project tools/SAW.Testing -- test --profile integration --filter "FullyQualifiedName~SupplierSqlIntegrationTests"
```

Filter bổ sung được giao với profile: muốn chạy ca browser phải chọn browser/all.
Runner luôn build mã hiện tại. Wrapper `scripts/test-local.sh` và `scripts/test-local.ps1`
gọi all, nên vẫn cần Node 22 và Cloudinary. CLI trên là đường chạy chung cho mọi OS.

## 7. Đọc kết quả

Mỗi lượt có thư mục mới để không đọc nhầm TRX cũ:

```text
test-results/<profile>/<run-id>/all-tests.trx
test-results/<profile>/<run-id>/console.log
test-results/<profile>/<run-id>/<ảnh và JSON minh chứng khi có>
```

Đường dẫn đầy đủ được in đầu/cuối lượt; run-id dùng UTC. Mở TRX bằng Visual Studio
hoặc editor XML, tìm `outcome="Failed"`, đọc Message/StackTrace. Nếu build lỗi, có
thể chưa có TRX; đọc Terminal. Runner chỉ trả exit code 0 nếu số test >0, mọi test
được chọn passed và **0 skipped**. So tổng theo đúng profile, không so unit với all.

## 8. Sau khi pull/đổi nhánh

```text
git pull
cd ../Capstone_SAW_FE
git pull
cd ../Capstone_SAW_BE
dotnet run --project tools/SAW.Testing -- setup --existing --profile browser
dotnet run --project tools/SAW.Testing -- test --profile browser
```

Nếu SQL Compose đã dừng, dùng setup --docker thay cho --existing để start và chờ
healthcheck. Setup browser chạy lại npm ci, phù hợp khi package-lock.json thay đổi.
Init không ghi đè cấu hình và không cần xin lại mật khẩu sau mỗi lần pull.

Dừng riêng SQL do Compose tạo bằng `docker stop saw-tests-sql-1`; volume vẫn giữ.
Chạy setup --docker để start lại. Không xóa toàn bộ Docker volumes. Đổi mật khẩu
trong file không tự đổi mật khẩu trong volume SQL cũ: khôi phục cấu hình đúng hoặc
xử lý riêng volume test có chủ đích; công cụ không tự xóa dữ liệu.

## 9. GitHub Actions và review nhóm

- Workflow **Tests** chạy khi push/PR: unit matrix Windows/macOS/Ubuntu; SQL integration
  trên Ubuntu với Docker riêng. Không cần FE hay secret Cloudinary.
- Workflow **Browser and external tests** chạy thủ công trong tab Actions: nhập repo
  FE dạng owner/repository, chọn browser/all. Workflow checkout hai repo cạnh nhau,
  có thể điền frontend_ref là branch/tag/commit FE tương thích (để trống dùng nhánh mặc định),
  cài Node 22 và SQL mới. FE private cần secret `SAW_REPOS_READ_TOKEN` có quyền read FE.
  All cần ba repository secrets Cloudinary cùng tên với biến local.
- TRX/log/ảnh được lưu thành Actions artifacts kể cả khi lỗi. Không đưa secret vào YAML.

[GitHub yêu cầu Linux runner cho Docker service/container jobs](https://docs.github.com/en/actions/tutorials/use-containerized-services/use-docker-service-containers).
Unit kiểm tra cả ba OS; SQL CI chuẩn hóa trên Linux x64. Tạo YAML local chưa có nghĩa
CI đã chạy: cần nhóm commit/push rồi xem kết quả Actions.

Commit tool/script/schema/package/workflow/tài liệu cùng thay đổi nghiệp vụ. Không
commit .env.test.local, DB dump, node_modules, bin/obj hoặc test-results. Ghi rõ profile
và commit BE-FE đã kiểm chứng trong PR; browser passed không thay bằng chứng all.

## 10. Xử lý lỗi thường gặp

| Hiện tượng | Cách xử lý |
|---|---|
| Thiếu SDK 8 | Cài .NET 8 SDK, mở Terminal mới, kiểm tra dotnet --list-sdks |
| Docker daemon chưa chạy | Start Desktop/Engine, kiểm tra docker version |
| Compose không hiểu --wait | Cập nhật Compose v2, khuyến nghị 2.20+ |
| Port test bận | Dùng SQL đó với --existing hoặc chọn port khác |
| Login failed | Kiểm tra mật khẩu của volume SQL đang tồn tại, không tự xóa volume |
| CREATE DATABASE bị từ chối | Cấp quyền test-user trên SQL test; chạy lại setup probe |
| SQL crash/handshake trên ARM | Xem Docker logs; dùng SQL x64 từ xa nếu giả lập không ổn định |
| Sai Node | Cài/chọn Node 22 >=22.22.0, kiểm tra PATH bằng node --version |
| Không tìm được browser | Điền EDGE_PATH đúng executable, giữ nháy quanh đường dẫn có dấu cách |
| Thiếu FE/Vite | Đặt repo cạnh backend đúng tên; setup browser sẽ npm ci |
| Cloudinary lỗi | Kiểm tra test credentials/quota/network; browser không kiểm chứng dịch vụ này |
| Filter không chọn được ca | Chọn đúng profile/FullyQualifiedName; runner không chấp nhận 0 test |

Các lệnh được thiết kế cho ba OS; kết quả chạy trực tiếp và nền tảng chờ CI xác nhận
được báo riêng. Máy thiếu dependency hoặc dịch vụ ngoài đang lỗi không thể được bảo đảm full passed.

Xem [biên bản kiểm chứng](Testing-Verification.md) để biết những môi trường đã chạy thực tế.
