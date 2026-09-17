using SAW.Application.Exceptions;
using SAW.Domain.Entities;
namespace SAW.Application.Features.InspectionStandards;
public sealed class InspectionStandardService(IInspectionStandardRepository repository) : IInspectionStandardService
{
 public Task<IReadOnlyList<InspectionStandardListItem>> ListAsync(string? search, CancellationToken token)=>repository.ListAsync(search?.Trim(),token);
 public async Task<InspectionStandardDto> CreateAsync(CreateInspectionStandardRequest request, CancellationToken token)
 {
  if(request.CropTypeId<=0 || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name)) throw new BadRequestException("Loại nông sản, mã và tên bộ tiêu chuẩn là bắt buộc.");
  if(request.VersionNo<1 || request.Criteria.Count==0) throw new BadRequestException("Phiên bản phải hợp lệ và cần có ít nhất một tiêu chí.");
  if(!await repository.CropTypeExistsAsync(request.CropTypeId,token)) throw new BadRequestException("Loại nông sản không hợp lệ hoặc đã ngừng hoạt động.");
  var code=request.Code.Trim().ToUpperInvariant(); if(await repository.CodeExistsAsync(code,token)) throw new ConflictException("Mã bộ tiêu chuẩn đã tồn tại.");
  if(request.Criteria.Any(x=>string.IsNullOrWhiteSpace(x.Code)||string.IsNullOrWhiteSpace(x.Name))) throw new BadRequestException("Mỗi tiêu chí cần có mã và tên.");
  if(request.Criteria.GroupBy(x=>x.Code.Trim(),StringComparer.OrdinalIgnoreCase).Any(x=>x.Count()>1)) throw new BadRequestException("Mã tiêu chí không được trùng lặp.");
  if(request.Criteria.Any(x=>x.MinValue.HasValue&&x.MaxValue.HasValue&&x.MinValue>x.MaxValue)) throw new BadRequestException("Giá trị tối thiểu không được lớn hơn tối đa.");
  var standard=new InspectionStandardSet { CropTypeId=request.CropTypeId,StandardCode=code,StandardName=request.Name.Trim(),Description=string.IsNullOrWhiteSpace(request.Description)?null:request.Description.Trim(),IsActive=true,CreatedAt=DateTime.UtcNow};
  var version=new InspectionStandardVersion { VersionNo=request.VersionNo,VersionStatus="DRAFT",EffectiveFrom=request.EffectiveFrom,CreatedAt=DateTime.UtcNow};
  foreach(var item in request.Criteria){var criterion=new InspectionCriterion { CriterionCode=item.Code.Trim().ToUpperInvariant(),CriterionName=item.Name.Trim(),CriterionGroup=string.IsNullOrWhiteSpace(item.CriterionGroup)?"GENERAL":item.CriterionGroup.Trim(),DataType=item.DataType.Trim(),Unit=string.IsNullOrWhiteSpace(item.Unit)?null:item.Unit.Trim(),IsRequired=item.IsRequired,IsCritical=item.IsCritical}; criterion.GradeRules.Add(new CriterionGradeRule { Grade="PASS",MinValue=item.MinValue,MaxValue=item.MaxValue,RequiredTextValue=string.IsNullOrWhiteSpace(item.RequiredTextValue)?null:item.RequiredTextValue.Trim(),IsFailRule=item.IsFailRule});version.Criteria.Add(criterion);}
  standard.Versions.Add(version);repository.Add(standard);await repository.SaveChangesAsync(token);return new(standard.InspectionStandardSetId,standard.StandardCode,standard.StandardName,standard.CropTypeId,version.VersionNo,version.Criteria.Count);
 }
}
