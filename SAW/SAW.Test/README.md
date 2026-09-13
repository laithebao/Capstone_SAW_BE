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

# Chạy filter theo category
dotnet test SAW.Test --filter "Category=Unit"
```

---

## 📦 Dependencies

→ **Project references:** `SAW.Domain`, `SAW.Application`
