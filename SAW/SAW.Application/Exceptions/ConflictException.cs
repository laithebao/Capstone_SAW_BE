namespace SAW.Application.Exceptions;

/// <summary>
/// Ném khi có xung đột dữ liệu (e.g. duplicate email, trùng mã).
/// Maps tới HTTP 409 Conflict.
/// </summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message) { }
}
