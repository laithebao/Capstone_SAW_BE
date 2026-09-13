# SAW.Application – Application Layer

> **Chứa toàn bộ business logic. Không phụ thuộc vào Infrastructure hay API.**

---

## 📌 Vai trò

- Định nghĩa các **use case** (business operations) của hệ thống.
- Khai báo các **interface repository** (contract) – Implementation ở Infrastructure.
- Cung cấp **custom exceptions** để biểu diễn các tình huống lỗi nghiệp vụ.
- Đăng ký **FluentValidation validators** tự động.

---

## 📁 Cấu trúc thư mục

```
SAW.Application/
├── Features/                    ← Use cases, chia theo domain
│   └── <Feature>/
│       ├── DTOs/                ← Data Transfer Objects
│       ├── Queries/             ← Truy vấn dữ liệu (GET)
│       └── Commands/            ← Thay đổi dữ liệu (POST/PUT/DELETE)
│
├── Repositories/                ← Interface repositories (contracts)
│   └── I<Entity>Repository.cs
│
├── Exceptions/                  ← Custom business exceptions
│   ├── NotFoundException.cs     → HTTP 404
│   ├── BadRequestException.cs   → HTTP 400
│   ├── ValidationException.cs   → HTTP 422
│   ├── ConflictException.cs     → HTTP 409
│   └── ForbiddenException.cs    → HTTP 403
│
└── Extensions/
    └── ServiceCollectionExtensions.cs  ← DI: đăng ký validators
```

---

## 🚨 Custom Exceptions

Ném exception từ service/handler, `GlobalExceptionHandler` sẽ tự động bắt và trả về HTTP response chuẩn.

| Exception | HTTP Status | Khi nào dùng |
|---|---|---|
| `NotFoundException` | 404 | Entity không tồn tại |
| `BadRequestException` | 400 | Logic nghiệp vụ không hợp lệ |
| `ValidationException` | 422 | Dữ liệu đầu vào không hợp lệ |
| `ConflictException` | 409 | Trùng lặp dữ liệu (email, mã, ...) |
| `ForbiddenException` | 403 | Không có quyền thực hiện |

### Ví dụ sử dụng

```csharp
// Trong service / handler
var user = await _userRepository.GetByIdAsync(id);
if (user is null)
    throw new NotFoundException("Account", id);

if (await _userRepository.EmailExistsAsync(email))
    throw new ConflictException($"Email '{email}' already exists.");

if (request.StartDate >= request.EndDate)
    throw new BadRequestException("Start date must be before end date.");
```

---

## 🔍 Features (Use Cases)

Mỗi feature được tổ chức theo **Vertical Slice**:

```
Features/
└── Accounts/
    ├── DTOs/
    │   └── AccountDto.cs
    ├── Queries/
    │   └── GetAccountByIdQuery.cs    ← Handler trả về AccountDto
    └── Commands/
        ├── CreateAccountCommand.cs   ← Handler tạo account mới
        └── UpdateAccountCommand.cs
```

### Quy ước đặt tên

| Loại | Hậu tố | Ví dụ |
|---|---|---|
| Truy vấn | `Query` | `GetProductBatchByIdQuery` |
| Lệnh thêm/sửa/xóa | `Command` | `CreateProductBatchCommand` |
| Data transfer object | `Dto` | `ProductBatchDto` |
| Validator | `Validator` | `CreateProductBatchValidator` |

---

## 📦 Dependencies

| Package | Phiên bản | Mục đích |
|---|---|---|
| `FluentValidation` | 12.1.1 | Validation |
| `FluentValidation.DependencyInjectionExtensions` | 12.1.1 | Auto-register validators |
| `AutoMapper` | 16.1.1 | Object mapping |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | DI abstractions |

→ **Project reference:** `SAW.Domain`
