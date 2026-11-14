using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SAW.API.Controllers;
using SAW.Application.Features.SupplyContracts;

namespace SAW.Test.Application.SupplyContracts;

public sealed class SupplyContractsControllerTests
{
    private static SupplyContractsController Controller(ISupplyContractService service, string? id = "7", string claim = "account_id", string role = "WAREHOUSE_MANAGER") => new(service)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity(id is null ? [] : [new Claim(claim, id), new Claim(ClaimTypes.Role, role)], "Test")) } }
    };

    [Theory]
    [InlineData(0, "contract.pdf")]
    [InlineData(-1, "contract.pdf")]
    [InlineData(10485761, "contract.pdf")]
    [InlineData(20, "contract.pdf.exe")]
    [InlineData(20, "contract.txt")]
    [InlineData(20, "contract")]
    public async Task InvalidAttachmentIsRejectedBeforeOpeningStreamOrCallingService(long length, string name)
    {
        var service = new Mock<ISupplyContractService>(MockBehavior.Strict); var file = new Mock<IFormFile>(MockBehavior.Strict);
        file.SetupGet(f => f.Length).Returns(length); file.SetupGet(f => f.FileName).Returns(name);
        Assert.IsType<BadRequestObjectResult>((await Controller(service.Object).UploadAttachment(10, file.Object, default)).Result);
        file.Verify(f => f.OpenReadStream(), Times.Never); service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingAttachmentIsRejectedWithoutCallingService()
    {
        var service = new Mock<ISupplyContractService>(MockBehavior.Strict);
        Assert.IsType<BadRequestObjectResult>((await Controller(service.Object).UploadAttachment(10, null!, default)).Result); service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("folder/contract.PDF", "application/pdf")]
    [InlineData("folder/contract.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("folder/contract.doc", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task AcceptedAttachmentPreservesBytesStripsDirectoryAndUsesServerContentType(string name, string expectedType)
    {
        var bytes = new byte[] { 1, 4, 9, 0, 255 }; using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "text/html" };
        var service = new Mock<ISupplyContractService>(MockBehavior.Strict); SupplyContractAttachment? captured = null; using var cancellation = new CancellationTokenSource();
        service.Setup(s => s.AttachFileAsync(7, 10, It.IsAny<SupplyContractAttachment>(), cancellation.Token))
            .Callback<int, long, SupplyContractAttachment, CancellationToken>((_, _, attachment, _) => captured = attachment).ReturnsAsync((SupplyContractDetail)null!);
        Assert.IsType<OkObjectResult>((await Controller(service.Object).UploadAttachment(10, file, cancellation.Token)).Result);
        Assert.NotNull(captured); Assert.Equal(Path.GetFileName(name), captured.FileName); Assert.Equal(expectedType, captured.ContentType); Assert.Equal(bytes, captured.Content);
        service.VerifyAll();
    }

    [Fact]
    public async Task AttachmentExactlyTenMegabytesIsAccepted()
    {
        var bytes = new byte[10 * 1024 * 1024]; bytes[^1] = 123; using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "contract.pdf");
        var service = new Mock<ISupplyContractService>(MockBehavior.Strict);
        service.Setup(s => s.AttachFileAsync(7, 10, It.Is<SupplyContractAttachment>(f => f.Content.Length == bytes.Length && f.Content[bytes.Length - 1] == 123), It.IsAny<CancellationToken>())).ReturnsAsync((SupplyContractDetail)null!);
        Assert.IsType<OkObjectResult>((await Controller(service.Object).UploadAttachment(10, file, default)).Result); service.VerifyAll();
    }

    [Theory]
    [InlineData("account_id", "SUPPLIER")]
    [InlineData(ClaimTypes.NameIdentifier, "ADMINISTRATOR")]
    public async Task DownloadPreservesFileAndPassesActorAndRoleForOwnershipCheck(string claim, string role)
    {
        var bytes = new byte[] { 0, 8, 255 }; var service = new Mock<ISupplyContractService>(MockBehavior.Strict);
        service.Setup(s => s.DownloadAttachmentAsync(7, role, 10, It.IsAny<CancellationToken>())).ReturnsAsync((bytes, "application/pdf", "contract.pdf"));
        var file = Assert.IsType<FileContentResult>(await Controller(service.Object, claim: claim, role: role).DownloadAttachment(10, default));
        Assert.Equal(bytes, file.FileContents); Assert.Equal("contract.pdf", file.FileDownloadName); Assert.Equal("application/pdf", file.ContentType); service.VerifyAll();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("not-an-id")]
    public async Task InvalidIdentityCannotDownloadAnAttachment(string? id)
    {
        var service = new Mock<ISupplyContractService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Controller(service.Object, id).DownloadAttachment(10, default)); service.VerifyNoOtherCalls();
    }
}
