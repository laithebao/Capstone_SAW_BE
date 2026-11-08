namespace SAW.Application.Exceptions;

/// <summary>
/// Ném khi request không hợp lệ về mặt logic nghiệp vụ (khác với validation).
/// Maps tới HTTP 400 Bad Request.
/// </summary>
public sealed class BadRequestException : Exception
{
    public IReadOnlyList<string>? Errors { get; }

    public BadRequestException(string message)
        : base(message) { }

    public BadRequestException(string message, IEnumerable<string> errors)
        : base(message)
    {
        Errors = errors.ToList();
    }
}
