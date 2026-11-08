# SAW.Infrastructure – Infrastructure Layer

> **Triển khai cụ thể cho database và các external services.**

---

## 📌 Vai trò

- **Implement** các interface repository được khai báo ở Application layer.
- Cấu hình **Entity Framework Core** với SQL Server.
- Quản lý **database migrations**.
- Cấu hình từng entity bằng **Fluent API** (IEntityTypeConfiguration).

---

## 📁 Cấu trúc thư mục

```
SAW.Infrastructure/
├── Persistence/
│   ├── AppDbContext.cs               ← EF Core DbContext (đăng ký 42 DbSets)
│   └── Configurations/               ← Fluent API configuration cho từng entity
│       ├── AccountConfiguration.cs
│       ├── ProductBatchConfiguration.cs
│       ├── InventoryConfiguration.cs
│       └── ... (42 files – 1 file per entity)
│
├── Repositories/                     ← Concrete implementations của IRepository
│   └── <Entity>Repository.cs
│
├── Migrations/                       ← EF Core auto-generated migrations
│   └── *.cs
│
└── Extensions/
    └── ServiceCollectionExtensions.cs  ← Đăng ký DbContext, repositories vào DI
```

---

## 🗄️ `AppDbContext`

EF Core DbContext kết nối SQL Server, chứa tất cả 42 `DbSet<T>`.

Được cấu hình trong `Extensions/ServiceCollectionExtensions.cs`:
- Connection string từ `appsettings.json` (`DefaultConnection`)
- Timeout: **60 giây**
- Retry on failure: **3 lần**, delay **10 giây**

---

## ⚙️ Entity Configurations

Mỗi entity có 1 file configuration riêng sử dụng **Fluent API** thay vì Data Annotations:

```csharp
// Ví dụ: ProductBatchConfiguration.cs
public class ProductBatchConfiguration : IEntityTypeConfiguration<ProductBatch>
{
    public void Configure(EntityTypeBuilder<ProductBatch> builder)
    {
        builder.HasKey(x => x.BatchId);
        builder.Property(x => x.BatchCode).IsRequired().HasMaxLength(50);
        // Relationships, indexes, ...
    }
}
```

---

## 🔄 Migrations

```bash
# Tạo migration mới
dotnet ef migrations add <MigrationName> \
    --project SAW.Infrastructure \
    --startup-project SAW.API

# Áp dụng migration lên database
dotnet ef database update \
    --project SAW.Infrastructure \
    --startup-project SAW.API

# Rollback về migration trước
dotnet ef database update <PreviousMigrationName> \
    --project SAW.Infrastructure \
    --startup-project SAW.API
```

---

## 📦 Dependencies

| Package | Phiên bản | Mục đích |
|---|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | 8.0.0 | EF Core SQL Server provider |
| `Microsoft.EntityFrameworkCore.Tools` | 8.0.0 | CLI tools (migrations) |
| `Microsoft.Extensions.Hosting.Abstractions` | 8.0.0 | IHostEnvironment |

→ **Project references:** `SAW.Domain`, `SAW.Application`
