using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SAW.Migrations;

try
{
    if (args.Length != 2 || args[0] is not ("generate" or "verify" or "apply"))
        throw new InvalidOperationException("Usage: SAW.Migrations (generate|verify|apply) <artifact directory>");
    var command = args[0];
    var directory = args[1];
    string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing environment variable {name}.");
    var commit = Env("GITHUB_SHA");
    var run = Env("GITHUB_RUN_ID");
    var attempt = Env("GITHUB_RUN_ATTEMPT");
    if (command == "generate")
    {
        var baseline = File.ReadAllText("scripts/ci/database-baseline.txt").Trim();
        var generated = MigrationPlan.Generate(directory, baseline, commit, run, attempt);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(summary))
            File.AppendAllText(summary, File.ReadAllText(Path.Combine(directory, "REVIEW.md")));
        Console.WriteLine($"Review artifact generated; migration target {generated.Head}.");
    }
    else
    {
        var plan = MigrationPlan.Load(directory, commit, run, attempt);
        if (command == "apply")
        {
            var connectionString = SqlMigration.Connection(Env("AZURE_SQL_MIGRATION_CONNECTION_STRING"),
                Env("AZURE_SQL_SERVER_FQDN"), Env("AZURE_SQL_DATABASE"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SAW-CI-CD");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Env("GH_TOKEN"));
            async Task<bool> Current()
            {
                using var response = await http.GetAsync($"https://api.github.com/repos/{Env("GITHUB_REPOSITORY")}/git/ref/heads/main");
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.GetProperty("object").GetProperty("sha").GetString() == commit;
            }
            if (!await Current()) throw new InvalidOperationException("Release is no longer the current main commit.");
            await using var connection = new SqlConnection(connectionString.ConnectionString);
            await SqlMigration.OpenAfterFirewallUpdate(connection);
            await SqlMigration.Apply(connection, plan, directory, "artifacts/migration-result.json", Current);
        }
        else Console.WriteLine($"Migration artifact verified; target {plan.Head}.");
    }
    return 0;
}
catch (SqlException error)
{
    // SQL exception messages can contain row data. Keep credentials and data out of public logs.
    Console.Error.WriteLine($"SQL migration failed (number {error.Number}, line {error.LineNumber}). Review Azure SQL and the approved script. API deployment is blocked; no database downgrade was attempted.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is InvalidOperationException ? error.Message : $"Migration failed: {error.GetType().Name}. Check the artifact and configuration; secrets are not logged.");
    return 1;
}
