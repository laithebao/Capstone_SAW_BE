using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAW.Application.Features.Suppliers.DTOs;

public class SupplierCertificationInputDto
{
    public string CertificationName { get; set; } = string.Empty;
    public string? EvidenceFileUrl { get; set; }
}

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

    [Phone(ErrorMessage = "Số điện thoại không hợp lệ.")]
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
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

    // Dùng JsonElement để hứng mọi định dạng từ Frontend (string[] hoặc object[])
    public JsonElement? Certifications { get; set; }

    // Trường tương thích với payload cũ của Frontend
    public List<string>? EvidenceDocumentUrls { get; set; }

    /// <summary>
    /// Chuyển đổi linh hoạt dữ liệu Certifications từ Frontend về kiểu SupplierCertificationInputDto chuẩn
    /// </summary>
    public List<SupplierCertificationInputDto> GetNormalizedCertifications()
    {
        var result = new List<SupplierCertificationInputDto>();

        if (Certifications.HasValue && Certifications.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in Certifications.Value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var certName = element.GetString();
                    if (!string.IsNullOrWhiteSpace(certName))
                    {
                        result.Add(new SupplierCertificationInputDto
                        {
                            CertificationName = certName
                        });
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    var dto = JsonSerializer.Deserialize<SupplierCertificationInputDto>(
                        element.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (dto != null && !string.IsNullOrWhiteSpace(dto.CertificationName))
                    {
                        result.Add(dto);
                    }
                }
            }
        }

        // Ghép file đính kèm từ EvidenceDocumentUrls nếu Frontend gửi riêng
        if (EvidenceDocumentUrls != null && EvidenceDocumentUrls.Any())
        {
            for (int i = 0; i < EvidenceDocumentUrls.Count; i++)
            {
                var url = EvidenceDocumentUrls[i];
                if (i < result.Count)
                {
                    result[i].EvidenceFileUrl = url;
                }
                else
                {
                    result.Add(new SupplierCertificationInputDto
                    {
                        CertificationName = "Chứng nhận đính kèm",
                        EvidenceFileUrl = url
                    });
                }
            }
        }

        return result;
    }
}

public class UpdateSupplierProfileRequest : DeclareSupplierProfileRequest
{
    // Kế thừa toàn bộ thuộc tính và hàm chuẩn hóa dữ liệu từ DeclareSupplierProfileRequest
}