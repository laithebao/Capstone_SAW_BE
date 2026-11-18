using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure;

internal static class WarehouseWorkflowTestSchema
{
    internal static async Task InstallAsync(AppDbContext db, string resourceName, bool replaceExistingTriggers = false)
    {
        // This helper must never install fixture definitions into a supplied/source database.
        var databaseName = db.Database.GetDbConnection().Database;
        if (!Regex.IsMatch(databaseName, "^SAW_UC28_Test_[a-f0-9]{32}$"))
            throw new InvalidOperationException("Workflow test schema requires an isolated ReceivingSqlFixture database.");

        using var stream = typeof(WarehouseWorkflowTestSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded workflow test schema: {resourceName}");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();
        // Migration-based fixtures already contain some workflow triggers. Install the
        // reviewed definitions without relying on objects in the connection's source DB.
        if (replaceExistingTriggers)
            script = Regex.Replace(script, @"\bCREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\b", "CREATE OR ALTER TRIGGER", RegexOptions.IgnoreCase);

        // Keep SET options and CREATE TRIGGER batches on the same connection.
        await db.Database.OpenConnectionAsync();
        try
        {
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
