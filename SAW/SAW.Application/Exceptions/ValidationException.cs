namespace SAW.Application.Exceptions;

/// <summary>
/// Ném khi FluentValidation phát hiện lỗi validation.
/// Maps tới HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class ValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ValidationException(IEnumerable<string> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors.ToList();
    }

    /// <summary>Tạo từ FluentValidation.Results.ValidationResult.</summary>
    public static ValidationException FromFluentValidation(
        FluentValidation.Results.ValidationResult result)
        => new(result.Errors.Select(e => e.ErrorMessage));
}
