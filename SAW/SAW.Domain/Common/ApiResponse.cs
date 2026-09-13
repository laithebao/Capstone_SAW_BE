namespace SAW.Domain.Common;

/// <summary>Chuẩn response trả về cho toàn bộ API.</summary>
public sealed class ApiResponse<T>
{
    public int StatusCode { get; private set; }
    public bool IsSuccess { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public T? Data { get; private set; }
    public IReadOnlyList<string>? Errors { get; private set; }
    public DateTime Timestamp { get; private set; } = DateTime.UtcNow;

    private ApiResponse() { }

    // ── Success ───────────────────────────────────────────────────────────────

    public static ApiResponse<T> Success(T data, string message = "Success")
        => new() { StatusCode = 200, IsSuccess = true, Message = message, Data = data };

    public static ApiResponse<T> Created(T data, string message = "Created successfully")
        => new() { StatusCode = 201, IsSuccess = true, Message = message, Data = data };

    public static ApiResponse<T> NoContent(string message = "Operation completed")
        => new() { StatusCode = 204, IsSuccess = true, Message = message };

    // ── Failure ───────────────────────────────────────────────────────────────

    public static ApiResponse<T> BadRequest(string message, IEnumerable<string>? errors = null)
        => new() { StatusCode = 400, IsSuccess = false, Message = message, Errors = errors?.ToList() };

    public static ApiResponse<T> Unauthorized(string message = "Unauthorized")
        => new() { StatusCode = 401, IsSuccess = false, Message = message };

    public static ApiResponse<T> Forbidden(string message = "Forbidden")
        => new() { StatusCode = 403, IsSuccess = false, Message = message };

    public static ApiResponse<T> NotFound(string message = "Resource not found")
        => new() { StatusCode = 404, IsSuccess = false, Message = message };

    public static ApiResponse<T> Conflict(string message = "Conflict")
        => new() { StatusCode = 409, IsSuccess = false, Message = message };

    public static ApiResponse<T> UnprocessableEntity(string message, IEnumerable<string>? errors = null)
        => new() { StatusCode = 422, IsSuccess = false, Message = message, Errors = errors?.ToList() };

    public static ApiResponse<T> InternalServerError(string message = "An unexpected error occurred")
        => new() { StatusCode = 500, IsSuccess = false, Message = message };

    public static ApiResponse<T> Failure(int statusCode, string message, IEnumerable<string>? errors = null)
        => new() { StatusCode = statusCode, IsSuccess = false, Message = message, Errors = errors?.ToList() };
}

/// <summary>Non-generic shorthand khi không cần data (e.g. delete).</summary>
public static class ApiResponse
{
    public static ApiResponse<T> Success<T>(T data, string message = "Success")
        => ApiResponse<T>.Success(data, message);

    public static ApiResponse<object> Success(string message = "Success")
        => ApiResponse<object>.Success(new { }, message);

    public static ApiResponse<object> NoContent(string message = "Operation completed")
        => ApiResponse<object>.NoContent(message);

    public static ApiResponse<object> BadRequest(string message, IEnumerable<string>? errors = null)
        => ApiResponse<object>.BadRequest(message, errors);

    public static ApiResponse<object> Unauthorized(string message = "Unauthorized")
        => ApiResponse<object>.Unauthorized(message);

    public static ApiResponse<object> Forbidden(string message = "Forbidden")
        => ApiResponse<object>.Forbidden(message);

    public static ApiResponse<object> NotFound(string message = "Resource not found")
        => ApiResponse<object>.NotFound(message);

    public static ApiResponse<object> Conflict(string message = "Conflict")
        => ApiResponse<object>.Conflict(message);

    public static ApiResponse<object> InternalServerError(string message = "An unexpected error occurred")
        => ApiResponse<object>.InternalServerError(message);
}
