# SAW.API – Presentation Layer

> **Entry point của ứng dụng. Tiếp nhận HTTP request và trả về HTTP response.**

---

## 📌 Vai trò

- Định nghĩa các **API Controllers** (endpoints).
- Xử lý **middleware pipeline** (auth, exception handling, ...).
- Cấu hình **Dependency Injection** toàn ứng dụng.
- Tích hợp **Swagger UI** để document và test API.

---

## 📁 Cấu trúc thư mục

```
SAW.API/
├── Controllers/                      ← API Controllers (endpoints)
│   └── <Feature>Controller.cs
│
├── Middleware/
│   └── GlobalExceptionHandler.cs     ← Bắt & chuẩn hoá mọi unhandled exception
│
├── Properties/
│   └── launchSettings.json           ← Cấu hình port khi chạy local
│
├── appsettings.json                  ← Cấu hình production
├── appsettings.Development.json      ← Cấu hình development (override)
└── Program.cs                        ← Khởi tạo app, đăng ký services & middleware
```

---

## 🔌 Middleware Pipeline

Thứ tự middleware trong `Program.cs` (quan trọng):

```
Request
   │
   ▼
UseExceptionHandler()     ← 1. Bắt mọi exception (phải đứng đầu)
   │
   ▼
UseHttpsRedirection()     ← 2. Redirect HTTP → HTTPS
   │
   ▼
UseAuthentication()       ← 3. Xác thực JWT (khi đã thêm)
   │
   ▼
UseAuthorization()        ← 4. Kiểm tra quyền
   │
   ▼
MapControllers()          ← 5. Route tới Controller
   │
   ▼
Response
```

---

## 🚨 GlobalExceptionHandler

Implement `IExceptionHandler` (.NET 8), tự động bắt exception và map sang HTTP response chuẩn:

| Exception | HTTP Status |
|---|---|
| `NotFoundException` | 404 Not Found |
| `BadRequestException` | 400 Bad Request |
| `ValidationException` | 422 Unprocessable Entity |
| `ConflictException` | 409 Conflict |
| `ForbiddenException` | 403 Forbidden |
| `UnauthorizedAccessException` | 401 Unauthorized |
| Bất kỳ exception khác | 500 Internal Server Error |

**Log tự động:**
- `500+` → `LogError` (kèm stack trace)
- `400-499` → `LogWarning`

---

## ⚙️ Cấu hình `appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=...;Database=SmartAgriWarehouseDB;..."
  },
  "JwtSettings": {
    "SecretKey": "...",
    "Issuer": "SAW.API",
    "Audience": "SAW.Client",
    "ExpiryMinutes": 60
  },
  "Serilog": {
    "MinimumLevel": "Information"
  }
}
```

---

## 🎮 Controllers – Quy ước

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductBatchesController : ControllerBase
{
    // GET api/product-batches/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _service.GetByIdAsync(id);
        return Ok(ApiResponse<ProductBatchDto>.Success(result));
    }

    // POST api/product-batches
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductBatchRequest request)
    {
        var result = await _service.CreateAsync(request);
        return CreatedAtAction(nameof(GetById), new { id = result.Id },
            ApiResponse<ProductBatchDto>.Created(result));
    }
}
```

---

## 📦 Dependencies

| Package | Phiên bản | Mục đích |
|---|---|---|
| `Swashbuckle.AspNetCore` | 6.6.2 | Swagger UI |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 8.0.8 | JWT Auth |
| `Serilog.AspNetCore` | 8.0.3 | Structured logging |
| `Serilog.Sinks.Console` | 6.0.0 | Log ra console |
| `Serilog.Sinks.File` | 6.0.0 | Log ra file |
| `Microsoft.EntityFrameworkCore.Tools` | 8.0.0 | EF CLI |

→ **Project references:** `SAW.Application`, `SAW.Infrastructure`
