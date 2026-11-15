using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SAW.Infrastructure.Persistence;

namespace SAW.Migrations;

public sealed record MigrationStep(string Id, string File, string Sha256);

public sealed record MigrationPlan(string Commit, string RunId, string RunAttempt,
    string Baseline, string[] Migrations, MigrationStep[] Steps)
{
    public string Head => Migrations.Last();
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static MigrationPlan Generate(string directory, string baseline, string commit, string runId, string attempt)
    {
        // Offline context: no API startup, secrets, database connection or background workers.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=offline;User Id=unused;Password=unused").Options);
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.IndexOf(migrations, baseline);
        if (index < 0) throw new InvalidOperationException("The imported database baseline is missing from the assembly.");
        Directory.CreateDirectory(directory);
        var steps = new List<MigrationStep>();
        var migrator = db.GetService<IMigrator>();
        for (var i = index + 1; i < migrations.Length; i++)
        {
            // Generate once in CI; apply this exact file after approval. Never generate Down SQL.
            var sql = migrator.GenerateScript(migrations[i - 1], migrations[i]);
            ParseBatches(sql); // Reject unsupported SQL before uploading the review artifact.
            var file = migrations[i] + ".sql";
            File.WriteAllText(Path.Combine(directory, file), sql);
            steps.Add(new(migrations[i], file, Hash(File.ReadAllBytes(Path.Combine(directory, file)))));
        }
        var plan = new MigrationPlan(commit, runId, attempt, baseline, migrations, steps.ToArray());
        plan.Validate(directory, commit, runId, attempt);
        File.WriteAllText(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan, Json));
        var review = $"# Database migration review\n\nCommit: `{commit}`\n\nRun: `{runId}`, attempt: `{attempt}`\n\n" +
            $"Imported baseline: `{baseline}` (fixed; do not advance for new migrations).\n\nTarget: `{plan.Head}`\n\n" +
            "The release reads Azure SQL history and executes only missing scripts in this plan, in order.\n" +
            "Review all scripts after the database's actual current migration before approving.\n" +
            "Confirm compatibility with the running API AND the previous image used for rollback.\n" +
            "Use staged expand/contract releases for breaking changes. No automatic database rollback.\n\n" +
            (steps.Count == 0 ? "No migrations after the imported baseline. Database history is still verified.\n" :
                string.Join("\n", steps.Select(s => $"- `{s.File}` SHA256 `{s.Sha256}`")) + "\n");
        File.WriteAllText(Path.Combine(directory, "REVIEW.md"), review);
        return plan;
    }

    public static MigrationPlan Load(string directory, string commit, string runId, string attempt)
    {
        var plan = JsonSerializer.Deserialize<MigrationPlan>(File.ReadAllText(Path.Combine(directory, "plan.json")), Json)
            ?? throw new InvalidOperationException("Missing migration plan.");
        plan.Validate(directory, commit, runId, attempt);
        return plan;
    }

    public void Validate(string directory, string commit, string runId, string attempt)
    {
        if (!Regex.IsMatch(commit, "^[0-9a-f]{40}$") || !Regex.IsMatch(runId, "^[0-9]+$") ||
            !Regex.IsMatch(attempt, "^[0-9]+$") || Commit != commit || RunId != runId || RunAttempt != attempt)
            throw new InvalidOperationException("Migration artifact belongs to another commit, run or attempt.");
        if (Migrations.Length == 0 || Migrations.Any(m => !Regex.IsMatch(m, "^[0-9]{14}_[A-Za-z0-9_]+$")) ||
            !Migrations.SequenceEqual(Migrations.Distinct().Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Invalid or unordered migration history in artifact.");
        var baselineIndex = Array.IndexOf(Migrations, Baseline);
        if (baselineIndex < 0 || !Steps.Select(s => s.Id).SequenceEqual(Migrations.Skip(baselineIndex + 1)))
            throw new InvalidOperationException("The plan must contain every migration after the imported baseline.");
        foreach (var step in Steps)
        {
            if (step.File != step.Id + ".sql") throw new InvalidOperationException("Unexpected migration file name.");
            var bytes = File.ReadAllBytes(Path.Combine(directory, step.File));
            if (Hash(bytes) != step.Sha256) throw new InvalidOperationException("Reviewed migration SQL checksum mismatch.");
            ParseBatches(System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    public MigrationStep[] Pending(string[] applied)
    {
        // Reject unknown/newer migrations, gaps and an empty DB. InitialCreate.Up is empty:
        // this project cannot bootstrap the imported legacy schema from migrations alone.
        if (applied.Length < Array.IndexOf(Migrations, Baseline) + 1 ||
            applied.Length > Migrations.Length || !applied.SequenceEqual(Migrations.Take(applied.Length)))
            throw new InvalidOperationException("Azure migration history is not a supported prefix at/after the imported baseline. Refusing schema changes or downgrade.");
        return Steps.Where(s => !applied.Contains(s.Id, StringComparer.Ordinal)).ToArray();
    }

    public static string[] ParseBatches(string sql)
    {
        // A regex split on GO corrupts multiline strings/comments (e.g. trigger definitions).
        var parsed = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        if (errors.Count != 0 || parsed is not TSqlScript script)
            throw new InvalidOperationException("Migration SQL cannot be parsed; review its syntax before deploying.");
        return script.Batches.Where(b => b.Statements.Count > 0)
            .Select(b => sql.Substring(b.StartOffset, b.FragmentLength)).ToArray();
    }
}
