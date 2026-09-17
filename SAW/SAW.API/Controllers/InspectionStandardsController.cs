using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.InspectionStandards;
using SAW.Domain.Common;
namespace SAW.API.Controllers;
[ApiController]
[Route("api/inspection-standards")]
[Authorize(Roles="ADMINISTRATOR")]
public sealed class InspectionStandardsController(IInspectionStandardService service):ControllerBase
{
 [HttpGet]
 public async Task<ActionResult<ApiResponse<IReadOnlyList<InspectionStandardListItem>>>> List([FromQuery]string? search,CancellationToken token)=>Ok(ApiResponse<IReadOnlyList<InspectionStandardListItem>>.Success(await service.ListAsync(search,token)));
 [HttpPost]
 public async Task<ActionResult<ApiResponse<InspectionStandardDto>>> Create(CreateInspectionStandardRequest request,CancellationToken token)
 {var result=await service.CreateAsync(request,token);return Created($"api/inspection-standards/{result.Id}",ApiResponse<InspectionStandardDto>.Created(result,"Đã tạo bộ tiêu chuẩn kiểm định."));}
}
