using SAW.Application.Exceptions;

namespace SAW.Application.Features.DistributorOrders;

public static class OrderReturnRules
{
    public static void EnsureRequestable(string status)
    {
        if (status is not ("ISSUED" or "DISPATCHED" or "DELIVERED"))
            throw new ConflictException("Chỉ yêu cầu trả hàng sau khi đơn đã xuất kho, đang giao hoặc đã nhận hàng.");
    }

    public static string RequiredNote(string? value)
    {
        var note = value?.Trim();
        if (string.IsNullOrWhiteSpace(note) || note.Length > 1000 || note.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new BadRequestException("Vui lòng nhập nội dung từ 1 đến 1000 ký tự.");
        return note;
    }

}
