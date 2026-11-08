namespace SAW.Application.Exceptions;

/// <summary>
/// Ném khi người dùng đã xác thực nhưng không có quyền thực hiện hành động.
/// Maps tới HTTP 403 Forbidden.
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : base(message) { }
}
