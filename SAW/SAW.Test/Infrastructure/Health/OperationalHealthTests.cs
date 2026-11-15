using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SAW.API.Health;

namespace SAW.Test.Infrastructure.Health;

public sealed class OperationalHealthTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task ProbesRemainAnonymousAndLivenessSurvivesDatabaseFailure(bool databaseReady, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Release:Commit"] = "tested-commit" });
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        builder.Services.AddHealthChecks()
            .AddCheck("database", () => databaseReady ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Server=private-server;Password=private-password"), tags: ["ready"])
            .AddCheck("optional-external-service", () => HealthCheckResult.Unhealthy());

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOperationalHealthChecks();
        app.MapGet("/protected", () => "private");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var liveJson = await live.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", liveJson.GetProperty("status").GetString());
        Assert.Equal("tested-commit", liveJson.GetProperty("release").GetString());

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(expected, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-server", body);
        Assert.DoesNotContain("private-password", body);
        using var readyJson = JsonDocument.Parse(body);
        Assert.Equal(databaseReady ? "Healthy" : "Unhealthy", readyJson.RootElement.GetProperty("status").GetString());

        using var protectedResponse = await client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-valid-connection-string")]
    public async Task MissingOrInvalidDatabaseConfigurationReturnsSanitizedFailure(string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString
        }).Build();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var check = new DatabaseReadinessCheck(configuration, provider.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }
}
