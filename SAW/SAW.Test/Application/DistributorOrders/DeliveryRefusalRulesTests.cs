using SAW.Application.Exceptions;
using SAW.Application.Features.DistributorOrders;

namespace SAW.Test.Application.DistributorOrders;

public sealed class DeliveryRefusalRulesTests
{
    [Theory]
    [InlineData("ISSUED")][InlineData("DISPATCHED")][InlineData("DELIVERED")][InlineData("PARTIALLY_REFUSED")]
    public void AllowsRefusalBeforeAndAfterReceipt(string status) => DeliveryRefusalRules.EnsureEligible(status);

    [Theory]
    [InlineData("PENDING")][InlineData("APPROVED")][InlineData("PARTIALLY_ISSUED")][InlineData("DELIVERY_REFUSED")][InlineData("RETURNED")]
    [InlineData("RESERVED")][InlineData("PARTIALLY_PICKED")][InlineData("PICKED")][InlineData("CANCELLED")][InlineData("REJECTED")]
    public void BlocksOrdersNotFullyShippedOrAlreadyClosed(string status) =>
        Assert.Throws<ConflictException>(() => DeliveryRefusalRules.EnsureEligible(status));

    [Fact]
    public void RequiresMeaningfulReasonAndRealImageHeaders()
    {
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ValidateReason(" "));
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ValidateReason(new string('x', 1001)));
        Assert.Equal("Hàng hỏng", DeliveryRefusalRules.ValidateReason(" Hàng hỏng "));
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ImageType("<script>test</script>"u8, ".jpg"));
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ImageType("%PDF-1.7"u8, ".pdf"));
        Assert.Equal("image/png", DeliveryRefusalRules.ImageType(new byte[] {137,80,78,71,13,10,26,10}, ".png").ContentType);
        Assert.Equal("image/jpeg", DeliveryRefusalRules.ImageType(new byte[] {255,216,255,0}, ".jpeg").ContentType);
        Assert.Equal("image/webp", DeliveryRefusalRules.ImageType("RIFF1234WEBP"u8, ".webp").ContentType);
    }

    [Fact]
    public void ReasonPreservesReadableLineBreaksButRejectsHiddenControlCharacters()
    {
        Assert.Equal("Hư hỏng\nNiêm phong rách", DeliveryRefusalRules.ValidateReason(" Hư hỏng\nNiêm phong rách "));
        Assert.Equal(1000, DeliveryRefusalRules.ValidateReason(new string('x', 1000)).Length);
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ValidateReason("Hư\0hỏng"));
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ValidateReason("Hư\u001bhỏng"));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".svg")]
    [InlineData(".exe")]
    public void ValidJpegHeaderCannotMasqueradeAsAnotherFileExtension(string extension) =>
        Assert.Throws<BadRequestException>(() => DeliveryRefusalRules.ImageType(new byte[] { 255, 216, 255, 0 }, extension));
}
