using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Exceptions;
using SAW.Domain.Common;
using System.Text.Json;

namespace SAW.API.Middleware;

/// <summary>
/// Global exception handler – bắt mọi unhandled exception trong pipeline,
/// chuẩn hoá về ApiResponse và ghi log qua ILogger.
/// Đăng ký bằng: app.UseExceptionHandler() + services.AddExceptionHandler()
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Map exception -> (statusCode, response)
        var (statusCode, response) = MapException(exception);

        // Ghi log ở mức phù hợp
        LogException(exception, statusCode, httpContext);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(response, _jsonOptions),
            cancellationToken);

        return true; // đã xử lý, không tiếp tục pipeline
    }

    // ── Exception mapping ─────────────────────────────────────────────────────

    private static (int statusCode, ApiResponse<object> response) MapException(Exception exception)
    {
        return exception switch
        {
            // 404 - Not Found
            NotFoundException ex => (
                StatusCodes.Status404NotFound,
                ApiResponse<object>.NotFound(ex.Message)),

            // 400 - Bad Request
            BadRequestException ex => (
                StatusCodes.Status400BadRequest,
                ApiResponse<object>.BadRequest(ex.Message, ex.Errors)),

            // 422 - Validation
            SAW.Application.Exceptions.ValidationException ex => (
                StatusCodes.Status422UnprocessableEntity,
                ApiResponse<object>.UnprocessableEntity(ex.Message, ex.Errors)),

            // 409 - Conflict
            ConflictException ex => (
                StatusCodes.Status409Conflict,
                ApiResponse<object>.Conflict(ex.Message)),

            // 403 - Forbidden
            ForbiddenException ex => (
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Forbidden(ex.Message)),

            // 401 - Unauthorized (khi throw từ business logic)
            UnauthorizedAccessException ex => (
                StatusCodes.Status401Unauthorized,
                ApiResponse<object>.Unauthorized(ex.Message)),

            // 500 - mọi exception không xác định
            _ => (
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.InternalServerError())
        };
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private void LogException(Exception exception, int statusCode, HttpContext ctx)
    {
        var path = ctx.Request.Path;
        var method = ctx.Request.Method;

        if (statusCode >= 500)
        {
            _logger.LogError(exception,
                "[{Method} {Path}] Unhandled exception: {ExceptionType} – {Message}",
                method, path, exception.GetType().Name, exception.Message);
        }
        else if (statusCode >= 400)
        {
            _logger.LogWarning(
                "[{Method} {Path}] Client error {StatusCode}: {ExceptionType} – {Message}",
                method, path, statusCode, exception.GetType().Name, exception.Message);
        }
    }
}
