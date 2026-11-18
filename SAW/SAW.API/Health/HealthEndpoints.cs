using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SAW.API.Health;

public static class HealthEndpoints
{
    public static void MapOperationalHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteResponseAsync
        }).AllowAnonymous();
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            release = configuration["Release:Commit"] ?? "local"
        }, context.RequestAborted);
    }
}
