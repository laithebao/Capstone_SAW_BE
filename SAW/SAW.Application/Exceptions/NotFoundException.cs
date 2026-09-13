namespace SAW.Application.Exceptions;

/// <summary>
/// Ném khi một entity được tìm kiếm nhưng không tồn tại trong hệ thống.
/// Maps tới HTTP 404 Not Found.
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message)
        : base(message) { }

    public NotFoundException(string entityName, object key)
        : base($"{entityName} with identifier \x27{key}\x27 was not found.") { }
}
