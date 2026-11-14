using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using SAW.API.Controllers;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.Suppliers;

public sealed class SupplierFilesControllerTests
{
    private sealed class FailedSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("Simulated persistence failure");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public AppDbContext Db { get; }
        public string Root { get; }
        public Fixture(bool failSave = false)
        {
            // Unit tests must also work in a standalone checkout or a CI directory named /work.
            // Do not create the directory yet: rejected uploads must leave no storage behind.
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "saw-supplier-files-" + Guid.NewGuid().ToString("N")));
            var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
            if (failSave) options.AddInterceptors(new FailedSave());
            Db = new AppDbContext(options.Options);
        }
        public SupplierFilesController Controller(int actor = 7)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SupplierFiles:StoragePath"] = Root }).Build();
            return new SupplierFilesController(Db, config, Mock.Of<IWebHostEnvironment>()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", actor.ToString())], "Test")) } } };
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(Root)) { foreach (var file in Directory.GetFiles(Root)) File.Delete(file); Directory.Delete(Root); }
        }
    }
    private static FormFile Upload(byte[] bytes, string name) => new(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    private static byte[] Bytes(string extension) => extension switch
    {
        ".pdf" => "%PDF-test"u8.ToArray(), ".png" => [137, 80, 78, 71, 13, 10, 26, 10, 0],
        ".jpg" => [255, 216, 255, 0], _ => "RIFF1234WEBPdata"u8.ToArray()
    };

    [Theory]
    [InlineData("DOCUMENT", ".pdf", "application/pdf")]
    [InlineData("document", ".png", "image/png")]
    [InlineData("DOCUMENT", ".jpg", "image/jpeg")]
    [InlineData("AVATAR", ".webp", "image/webp")]
    public async Task UploadPersistsDetectedTypeOwnerAndExactBytesUsingManagedStorageName(string purpose, string extension, string contentType)
    {
        await using var fixture = new Fixture(); var bytes = Bytes(extension);
        Assert.IsType<OkObjectResult>(await fixture.Controller().Upload(Upload(bytes, "folder/document" + extension), purpose));
        var saved = Assert.Single(await fixture.Db.Set<SupplierFile>().ToListAsync());
        Assert.Equal(7, saved.AccountId); Assert.Equal(purpose.ToUpperInvariant(), saved.Purpose); Assert.Equal(contentType, saved.ContentType);
        Assert.Equal("document" + extension, saved.FileName); Assert.Equal(bytes.Length, saved.ByteLength);
        Assert.Equal(saved.SupplierFileId.ToString("N") + extension, saved.StorageName); Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, saved.StorageName)));
    }

    [Theory]
    [InlineData("AVATAR", ".pdf")]
    [InlineData("DOCUMENT", ".webp")]
    [InlineData("OTHER", ".png")]
    public async Task PurposeRestrictionsRejectFileWithoutPersistingOrCreatingStorage(string purpose, string extension)
    {
        await using var fixture = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller().Upload(Upload(Bytes(extension), "file" + extension), purpose));
        Assert.Empty(await fixture.Db.Set<SupplierFile>().ToListAsync()); Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData("file.pdf")]
    [InlineData("file.png")]
    [InlineData("file.exe")]
    public async Task ExtensionAndClaimedContentTypeCannotSubstituteForFileSignature(string name)
    {
        await using var fixture = new Fixture(); var file = Upload("plain text"u8.ToArray(), name); file.Headers = new HeaderDictionary(); file.ContentType = "application/pdf";
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller().Upload(file));
        Assert.Empty(await fixture.Db.Set<SupplierFile>().ToListAsync()); Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10485761)]
    public async Task InvalidLengthDoesNotOpenStream(long length)
    {
        await using var fixture = new Fixture(); var file = new Mock<IFormFile>(MockBehavior.Strict); file.SetupGet(f => f.Length).Returns(length);
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller().Upload(file.Object)); file.Verify(f => f.OpenReadStream(), Times.Never);
    }

    [Fact]
    public async Task UploadRequiresActorAndDatabaseFailureRemovesTheNewPhysicalFile()
    {
        await using var fixture = new Fixture(failSave: true);
        Assert.IsType<UnauthorizedResult>(await fixture.Controller(0).Upload(Upload(Bytes(".pdf"), "file.pdf")));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Controller().Upload(Upload(Bytes(".pdf"), "file.pdf")));
        Assert.Empty(Directory.GetFiles(fixture.Root)); fixture.Db.ChangeTracker.Clear(); Assert.Empty(await fixture.Db.Set<SupplierFile>().ToListAsync());
    }

    [Fact]
    public async Task DocumentDownloadRequiresOwnershipAndExistingPhysicalFile()
    {
        await using var fixture = new Fixture(); await fixture.Controller().Upload(Upload(Bytes(".pdf"), "file.pdf"));
        var saved = await fixture.Db.Set<SupplierFile>().SingleAsync();
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller(8).Download(saved.SupplierFileId, default));
        var response = Assert.IsType<PhysicalFileResult>(await fixture.Controller().Download(saved.SupplierFileId, default));
        Assert.Equal("file.pdf", response.FileDownloadName); Assert.Equal(Path.Combine(fixture.Root, saved.StorageName), response.FileName);
        File.Delete(response.FileName); Assert.IsType<NotFoundObjectResult>(await fixture.Controller().Download(saved.SupplierFileId, default));
    }

    [Fact]
    public async Task AnonymousAvatarAccessRequiresSupplierLinkAndCurrentAccountAvatarUrl()
    {
        await using var fixture = new Fixture(); var controller = fixture.Controller(); await controller.Upload(Upload(Bytes(".png"), "avatar.png"), "AVATAR");
        var file = await fixture.Db.Set<SupplierFile>().SingleAsync(); var url = $"/api/supplier-files/{file.SupplierFileId}/avatar";
        var account = new Account { AccountId = 7, Username = "supplier", Email = "supplier@test.invalid", FullName = "Supplier", PasswordHash = "hash", AvatarUrl = url };
        fixture.Db.Add(account); await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller(0).Avatar(file.SupplierFileId, default));
        file.SupplierId = 1; await fixture.Db.SaveChangesAsync();
        Assert.IsType<PhysicalFileResult>(await fixture.Controller(0).Avatar(file.SupplierFileId, default));
        account.AvatarUrl = "/different-avatar"; await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller(0).Avatar(file.SupplierFileId, default));
        account.AvatarUrl = url; file.Purpose = "DOCUMENT"; await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller(0).Avatar(file.SupplierFileId, default));
    }
}
