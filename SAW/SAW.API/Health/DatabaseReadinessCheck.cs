using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SAW.Infrastructure.Persistence;

namespace SAW.API.Health;

// A successful SQL login alone does not mean the application schema is installed.
public sealed class DatabaseReadinessCheck(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Unhealthy("Database is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var connectionOptions = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = 2,
                ConnectRetryCount = 0
            };
            await using var connection = new SqlConnection(connectionOptions.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT CASE WHEN OBJECT_ID(N'dbo.ACCOUNT', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.PRODUCT_BATCH', N'U') IS NOT NULL
                    AND OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL
                    THEN 1 ELSE 0 END
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) != 1)
                return HealthCheckResult.Unhealthy("Application schema is not installed.");

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var requiredMigration = db.Database.GetMigrations().LastOrDefault();
            if (requiredMigration is null)
                return HealthCheckResult.Unhealthy("Application migration metadata is unavailable.");

            command.CommandText = "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = @migration";
            command.Parameters.AddWithValue("@migration", requiredMigration);
            return Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 1
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Application migrations are not current.");
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException
            or ArgumentException or InvalidOperationException)
        {
            // Keep credentials, server names and SQL exception details out of public probes.
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}
