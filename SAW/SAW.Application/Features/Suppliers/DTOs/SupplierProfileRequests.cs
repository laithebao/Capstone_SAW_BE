using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.ComponentModel.DataAnnotations;

namespace SAW.Application.Features.Suppliers.DTOs;

public class DeclareSupplierProfileRequest
{
    [Required(ErrorMessage = "Tên doanh nghiệp / hợp tác xã không được để trống.")]
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Mã số thuế / Mã số đăng ký không được để trống.")]
    [MaxLength(50)]
    public string TaxCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? SupplierType { get; set; }

    [Required(ErrorMessage = "Người đại diện pháp lý không được để trống.")]
    [MaxLength(100)]
    public string LegalRepresentative { get; set; } = string.Empty;

    [Required(ErrorMessage = "Người liên hệ không được để trống.")]
    [MaxLength(100)]
    public string ContactPerson { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Invalid phone number.")]
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    [EmailAddress(ErrorMessage = "Invalid email format.")]
    [MaxLength(150)]
    public string? Email { get; set; }

    public string? LogoUrl { get; set; }

    public string? Province { get; set; }
    public string? District { get; set; }
    public string? Ward { get; set; }

    [Required(ErrorMessage = "Địa chỉ không được để trống.")]
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    public decimal? FarmingAreaHa { get; set; }

    [MinLength(1, ErrorMessage = "Vui lòng chọn ít nhất một danh mục nông sản cung cấp.")]
    public List<int> CropTypeIds { get; set; } = new();

    public List<string> Certifications { get; set; } = new();

    public List<string>? EvidenceDocumentUrls { get; set; }

    public class UpdateSupplierProfileRequest
    {
        [Required(ErrorMessage = "Please fill in all required fields.")]
        [MaxLength(200)]
        public string SupplierName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please fill in all required fields.")]
        [MaxLength(50)]
        public string TaxCode { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? SupplierType { get; set; }

        [Required(ErrorMessage = "Please fill in all required fields.")]
        [MaxLength(100)]
        public string LegalRepresentative { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please fill in all required fields.")]
        [MaxLength(100)]
        public string ContactPerson { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Invalid phone number.")]
        [MaxLength(20)]
        public string? PhoneNumber { get; set; }

        [EmailAddress(ErrorMessage = "Invalid email format.")]
        [MaxLength(150)]
        public string? Email { get; set; }

        public string? LogoUrl { get; set; }

        public string? Province { get; set; }
        public string? District { get; set; }
        public string? Ward { get; set; }

        [Required(ErrorMessage = "Please fill in all required fields.")]
        [MaxLength(500)]
        public string Address { get; set; } = string.Empty;

        public decimal? FarmingAreaHa { get; set; }

        [MinLength(1, ErrorMessage = "Please fill in all required fields.")]
        public List<int> CropTypeIds { get; set; } = new();

        public List<string> Certifications { get; set; } = new();

        public List<string>? EvidenceDocumentUrls { get; set; }
    }
}
