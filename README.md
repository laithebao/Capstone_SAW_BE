# 🌾 Smart Agri Warehouse (SAW) – Backend

Backend API cho hệ thống **Quản lý Kho Nông sản Thông minh (Smart Agri Warehouse)**, xây dựng theo kiến trúc **Clean Architecture** trên nền tảng **.NET 8**.

---

## 📐 Kiến trúc tổng quan

```
┌─────────────────────────────────────────────────────────┐
│                      SAW.API                            │  ← Presentation Layer
│          Controllers │ Middleware │ Program.cs           │
└───────────────────────────┬─────────────────────────────┘
                            │ depends on
┌───────────────────────────▼─────────────────────────────┐
│                   SAW.Application                       │  ← Business Logic Layer
│        Features │ Exceptions │ Repositories (interface) │
└──────────┬────────────────────────────────┬─────────────┘
           │ depends on                     │ implemented by
┌──────────▼──────────┐       ┌─────────────▼─────────────┐
│     SAW.Domain      │       │     SAW.Infrastructure     │  ← Data Access Layer
│  Entities │ Common  │       │  EF Core │ Migrations │ DB │
└─────────────────────┘       └───────────────────────────┘
                    ▲
          ┌─────────┴─────────┐
          │     SAW.Test      │  ← Test Layer (xUnit)
          └───────────────────┘
```

> **Nguyên tắc phụ thuộc (Dependency Rule):** Các lớp ngoài phụ thuộc vào lớp trong. Domain không phụ thuộc vào bất kỳ lớp nào khác.

---

## 🗂️ Cấu trúc Solution

```
Capstone_SAW_BE/
├── README.md
├── SmartAgriWarehouseDB3.sql      ← Script khởi tạo database
└── SAW/
    ├── SAW.sln                    ← Visual Studio Solution
    ├── SAW.Domain/                ← Domain Layer
    ├── SAW.Application/           ← Application Layer
    ├── SAW.Infrastructure/        ← Infrastructure Layer
    ├── SAW.API/                   ← Presentation Layer
    └── SAW.Test/                  ← Test Layer
```

---

## 📦 Các Projects

### 1. `SAW.Domain` – Domain Layer
> **Tầng lõi, không phụ thuộc vào bất kỳ project nào khác.**

Chứa toàn bộ business entities, enums, và các common types dùng chung.

```
SAW.Domain/
├── Entities/          ← 42 entities (Account, ProductBatch, Inventory, ...)
└── Common/
    └── ApiResponse.cs ← Chuẩn response trả về cho toàn bộ API
```

📌 **Xem chi tiết:** [`SAW.Domain/README.md`](SAW/SAW.Domain/README.md)

---

### 2. `SAW.Application` – Application Layer
> **Chứa business logic, không phụ thuộc vào Infrastructure.**

Định nghĩa các use case, interface repository, exception nghiệp vụ, và validation.

```
SAW.Application/
├── Features/          ← Use cases (Commands / Queries / Handlers)
├── Repositories/      ← Interface repositories (contract)
├── Exceptions/        ← Custom business exceptions
│   ├── NotFoundException.cs
│   ├── BadRequestException.cs
│   ├── ValidationException.cs
│   ├── ConflictException.cs
│   └── ForbiddenException.cs
└── Extensions/
    └── ServiceCollectionExtensions.cs  ← DI registration
```

📌 **Xem chi tiết:** [`SAW.Application/README.md`](SAW/SAW.Application/README.md)

---

### 3. `SAW.Infrastructure` – Infrastructure Layer
> **Triển khai cụ thể cho database và external services.**

Implement các interface từ Application layer, cấu hình EF Core, migration.

```
SAW.Infrastructure/
├── Persistence/
│   ├── AppDbContext.cs               ← EF Core DbContext
│   └── Configurations/               ← Fluent API config cho 42 entities
├── Repositories/                     ← Concrete repository implementations
├── Migrations/                       ← EF Core database migrations
└── Extensions/
    └── ServiceCollectionExtensions.cs  ← Đăng ký DbContext, repositories
```

📌 **Xem chi tiết:** [`SAW.Infrastructure/README.md`](SAW/SAW.Infrastructure/README.md)

---

### 4. `SAW.API` – Presentation Layer
> **Entry point của ứng dụng. Xử lý HTTP request/response.**

```
SAW.API/
├── Controllers/       ← API Controllers
├── Middleware/
│   └── GlobalExceptionHandler.cs  ← Bắt & chuẩn hoá mọi exception
├── appsettings.json   ← Cấu hình ứng dụng
└── Program.cs         ← Khởi tạo DI container, middleware pipeline
```

📌 **Xem chi tiết:** [`SAW.API/README.md`](SAW/SAW.API/README.md)

---

### 5. `SAW.Test` – Test Layer
> **Unit tests & Integration tests.**

```
SAW.Test/
├── Domain/            ← Tests cho Domain layer
└── Application/       ← Tests cho Application layer (services, validators)
```

📌 **Xem chi tiết:** [`SAW.Test/README.md`](SAW/SAW.Test/README.md)

---

## 🛠️ Tech Stack

| Thành phần | Công nghệ |
|---|---|
| Framework | .NET 8 / ASP.NET Core 8 |
| ORM | Entity Framework Core 8 |
| Database | SQL Server |
| Validation | FluentValidation 12 |
| Mapping | AutoMapper 16 |
| Auth | JWT Bearer (Microsoft.AspNetCore.Authentication.JwtBearer) |
| Logging | Serilog (Console + File sinks) |
| API Docs | Swagger / Swashbuckle |
| Testing | xUnit + Moq + FluentAssertions |

---

## 🚀 Hướng dẫn chạy dự án

### Yêu cầu
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server (LocalDB hoặc SQL Server Express)

### 1. Clone & restore
```bash
git clone <repo-url>
cd Capstone_SAW_BE/SAW
dotnet restore
```

### 2. Cấu hình connection string
Mở `SAW.API/appsettings.json` và cập nhật:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=SmartAgriWarehouseDB;Trusted_Connection=True;"
  }
}
```

### 3. Tạo database
```bash
# Chạy migration
dotnet ef database update --project SAW.Infrastructure --startup-project SAW.API

# Hoặc chạy SQL script có sẵn
# Thực thi file: SmartAgriWarehouseDB3.sql
```

### 4. Chạy API
```bash
dotnet run --project SAW.API
```
API sẽ chạy tại: `https://localhost:7xxx` | Swagger UI: `https://localhost:7xxx/swagger`

### 5. Chạy tests
```bash
dotnet test SAW.Test
```

---

## 📋 API Response Format

Mọi API đều trả về format chuẩn:

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

---

## 👥 Team

Capstone Project – Smart Agri Warehouse System
