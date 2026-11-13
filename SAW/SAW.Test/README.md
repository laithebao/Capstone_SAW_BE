# SAW.Test – Test Layer

> **Unit tests và Integration tests cho toàn bộ hệ thống.**

---

## 📌 Vai trò

- Kiểm thử **business logic** ở Application layer.
- Kiểm thử **domain logic** và các common types.
- Đảm bảo chất lượng code khi refactor hoặc thêm feature mới.

---

## 📁 Cấu trúc thư mục

```
SAW.Test/
├── Domain/                      ← Tests cho SAW.Domain
│   └── Common/
│       └── ApiResponseTests.cs  ← Test ApiResponse<T> factory methods
│
└── Application/                 ← Tests cho SAW.Application
    ├── Exceptions/              ← Test custom exceptions
    └── Features/                ← Test use case handlers / services
        └── <Feature>/
            └── <Feature>ServiceTests.cs
```

---

## 🛠️ Tech Stack

| Package | Phiên bản | Mục đích |
|---|---|---|
| `xUnit` | 2.9.2 | Test framework |
| `Moq` | 4.20.72 | Mocking dependencies |
| `FluentAssertions` | 8.10.0 | Readable assertions |
| `coverlet.collector` | 6.0.2 | Code coverage |

---

## 🧪 Quy ước đặt tên test

### File
```
<ClassUnderTest>Tests.cs
```

### Method (theo pattern AAA)
```
<MethodName>_<Scenario>_<ExpectedResult>

// Ví dụ:
GetById_WhenUserNotFound_ThrowsNotFoundException()
Create_WhenEmailDuplicated_ThrowsConflictException()
ApiResponse_Success_ShouldReturnStatusCode200()
```

---

## ✍️ Ví dụ test

```csharp
public class ApiResponseTests
{
    [Fact]
    public void Success_ShouldReturn200WithData()
    {
        // Arrange
        var data = new { Name = "Test" };

        // Act
        var response = ApiResponse<object>.Success(data, "OK");

        // Assert
        response.StatusCode.Should().Be(200);
        response.IsSuccess.Should().BeTrue();
        response.Data.Should().Be(data);
        response.Errors.Should().BeNull();
    }

    [Fact]
    public void NotFound_ShouldReturn404()
    {
        var response = ApiResponse<object>.NotFound("User not found");

        response.StatusCode.Should().Be(404);
        response.IsSuccess.Should().BeFalse();
        response.Message.Should().Be("User not found");
    }
}
```

```csharp
public class AccountServiceTests
{
    private readonly Mock<IAccountRepository> _repoMock = new();

    [Fact]
    public async Task GetById_WhenNotExists_ThrowsNotFoundException()
    {
        // Arrange
        _repoMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Account?)null);
        var service = new AccountService(_repoMock.Object);

        // Act
        var act = async () => await service.GetByIdAsync(999);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*999*");
    }
}
```

---

## 🚀 Chạy tests

```bash
# Chạy tất cả tests
dotnet test SAW.Test

# Chạy với coverage report
dotnet test SAW.Test --collect:"XPlat Code Coverage"

# Chạy nhóm AI Chat và AI Forecast
dotnet test SAW.Test --filter 'FullyQualifiedName~Infrastructure.AiChat|FullyQualifiedName~Infrastructure.AiForecast'
```

Nhóm AI chạy unit test và test giao thức với phản hồi HTTP giả lập cho OpenAI và Qdrant. Các test SQL của chatbot và dự báo dùng cơ sở dữ liệu riêng do fixture tạo và xóa. Đặt `SAW_TEST_SQL_CONNECTION` trỏ tới SQL Server dành cho kiểm thử để chạy các ca này. xUnit báo bỏ qua nếu chưa có cấu hình.

### SQL test xuất kho, soạn hàng và điều chỉnh tồn

`GoodsIssueSqlFixture`, `PickingSqlFixture` và `StockAdjustmentSqlFixture` dùng server/tài khoản từ
`SAW_TEST_SQL_CONNECTION` để tạo và xóa database riêng có tên
`SAW_UC28_Test_<GUID>`. Với ba fixture này, phần `Database` của chuỗi kết nối
có thể là `master`: chúng không đọc trigger/check từ database nguồn.

Schema kiểm thử dùng các trigger/check đã được kiểm chứng, đóng gói trong
`PickingSchema.sql`, `GoodsIssueSchema.sql` và `StockAdjustmentSchema.sql` của project test.
Fixture xuất kho áp dụng SQL của migration `FixGoodsIssueWorkflowTriggers` như trước;
fixture điều chỉnh tồn vẫn chạy chuỗi EF migration thật trước khi cài các định nghĩa
workflow cũ. Tất cả chạy trong database test. Không thay đổi migration history hoặc
dữ liệu database ứng dụng.
Khi thay đổi schema nghiệp vụ, cần review các tài nguyên này cùng các test liên quan.

Chạy riêng test rollback audit từ thư mục `SAW`, sau khi đã đặt biến SQL:

```powershell
dotnet test SAW.Test/SAW.Test.csproj -c Release --filter "FullyQualifiedName=SAW.Test.Infrastructure.GoodsIssues.GoodsIssueSqlTests.Failed_audit_rolls_back_commit_inventory_reservations_order_batch_and_history"
```

Test vẫn phải kiểm tra lỗi audit được phát sinh và toàn bộ phiếu, tồn kho,
giữ hàng, trạng thái đơn/lô, lịch sử và thông báo được rollback. Không giảm
assertion, không đổi kết quả mong đợi và không bỏ qua test khi thiếu trigger nguồn.
Các fixture khác có thể vẫn đọc định nghĩa từ database nguồn; quy tắc dùng
`master` ở trên chỉ áp dụng cho ba fixture vừa nêu.

Hai script FE `tests/distributor-orders-browser.mjs` và
`tests/stock-adjustment-order-resolution-browser.mjs` khởi tạo phiên test qua
`localStorage` theo cơ chế đăng nhập hiện tại của FE. Chúng cần `SAW_TEST_BROWSER=1`,
FE đã cài dependencies và `EDGE_PATH` trỏ đến file Chrome/Edge thực thi.
Chạy từ thư mục `SAW` với cùng cấu hình SQL:

```powershell
dotnet test SAW.Test/SAW.Test.csproj -c Release --filter "FullyQualifiedName~Browser_Catalog_Create_List_Detail_Cancel_AndRoleGuards|FullyQualifiedName~Customer_accept_then_manager_finalizes_same_order_80kg_price_8500000_and_can_pick_issue_receive|FullyQualifiedName~Browser_staff_manager_customer_disposal_decline_and_accept_use_real_API_and_SQL"
```

---

## 📦 Dependencies

→ **Project references:** `SAW.Domain`, `SAW.Application`
