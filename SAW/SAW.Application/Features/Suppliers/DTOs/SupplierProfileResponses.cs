using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SAW.Application.Features.Suppliers.DTOs;

public class SupplierProfileResponse
{
    public int SupplierId { get; set; }
    public int AccountId { get; set; }

    // Thông tin cơ bản
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string TaxCode { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? OperatingRegion { get; set; } // Vùng hoạt động / GrowingArea
    public string ProfileStatus { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? SupplierType { get; set; } // Lấy từ Note hoặc phân loại mở rộng

    // Thông tin liên hệ
    public string ContactPerson { get; set; } = string.Empty;
    public string? LegalRepresentative { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }

    // Thông tin sản xuất
    public string? DetailedPlantingArea { get; set; }
    public decimal? FarmingAreaHa { get; set; }
    public List<SupplierCropTypeDto> CropTypes { get; set; } = new();
    public List<SupplierCertificationDto> Certifications { get; set; } = new();

    // File đính kèm
    public List<SupplierDocumentDto> Documents { get; set; } = new();
}

public class SupplierCropTypeDto
{
    public int CropTypeId { get; set; }
    public string CropCode { get; set; } = string.Empty;
    public string CropName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
}

public class SupplierCertificationDto
{
    public long SupplierCertificationId { get; set; }
    public string CertificationName { get; set; } = string.Empty;
    public string? CertificateNumber { get; set; }
    public string? IssuingOrganization { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? EvidenceFileUrl { get; set; }
    public bool IsActive { get; set; }
}

public class SupplierDocumentDto
{
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string? FileType { get; set; }
    public double? FileSizeMb { get; set; }
}