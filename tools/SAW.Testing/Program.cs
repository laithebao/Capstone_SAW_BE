using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SAW.Testing;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SAW", "SAW.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate backend root containing SAW/SAW.sln.");
        var command = args.FirstOrDefault() ?? "help";
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--docker" or "--existing") options[args[i]] = "true";
            else if (args[i] is "--env-file" or "--profile" or "--filter" or "--port")
            {
                var key = args[i];
                if (++i == args.Length) throw new ArgumentException($"Missing value for {key}.");
                options[key] = args[i];
            }
            else throw new ArgumentException($"Unknown option {args[i]}.");
        }
        var envFile = Path.GetFullPath(options.GetValueOrDefault("--env-file", ".env.test.local"), root.FullName);
        if (command == "help")
        {
            Console.WriteLine("SAW.Testing: init [--port 1434] | setup (--docker|--existing) | test [--profile unit|integration|browser|all] [--filter expression]\nAll commands support --env-file path. See docs/Testing.md.");
            return 0;
        }
        if (command == "init")
        {
            if (File.Exists(envFile)) { Console.WriteLine($"Kept existing configuration: {envFile}"); return 0; }
            var port = int.Parse(options.GetValueOrDefault("--port", "1434"));
            if (port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
            var password = "Saw1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var example = await File.ReadAllTextAsync(Path.Combine(root.FullName, ".env.test.example"));
            example = example.Replace("REPLACE_WITH_A_STRONG_LOCAL_PASSWORD", password).Replace("127.0.0.1,1434", $"127.0.0.1,{port}");
            Directory.CreateDirectory(Path.GetDirectoryName(envFile)!);
            await File.WriteAllTextAsync(envFile, example, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(envFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine($"Created {envFile} with a random local SQL password. Do not commit this file.");
            return 0;
        }

        var env = TestConfig.Load(envFile);
        foreach (var name in new[] { "SAW_TEST_SQL_CONNECTION", "SAW_TEST_CLOUD_KEY", "SAW_TEST_CLOUD_SECRET" })
            if (env.TryGetValue(name, out var secret) && !string.IsNullOrWhiteSpace(secret)) Processes.Secrets.Add(secret);
        var profile = options.GetValueOrDefault("--profile", "browser");
        if (profile is not ("unit" or "integration" or "browser" or "all")) throw new ArgumentException("Unknown profile. Choose unit, integration, browser or all.");
        var browser = profile is "browser" or "all";
        if (command == "setup")
        {
            if (options.ContainsKey("--docker") == options.ContainsKey("--existing"))
                throw new ArgumentException("Choose exactly one of --docker or --existing.");
            var connection = Connection(env);
            if (options.ContainsKey("--docker"))
            {
                var address = connection.DataSource.Split(',');
                if (connection.IntegratedSecurity || connection.UserID != "sa" || address.Length != 2 ||
                    address[0] is not ("127.0.0.1" or "localhost") || !int.TryParse(address[1], out _))
                    throw new InvalidOperationException("Docker setup requires Server=127.0.0.1,<port> and User Id=sa. Use --existing for another SQL Server.");
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    Console.WriteLine("ARM64: Docker will emulate the x64 SQL image. For a Microsoft-supported SQL host, use --existing with a remote x64 server.");
                var cpuInfo = await Processes.Run("docker", ["info", "--format", "{{.NCPU}}"], root.FullName, env, quiet: true);
                if (cpuInfo.Code != 0 || !int.TryParse(cpuInfo.Output.Trim(), out var dockerCpus))
                    throw new InvalidOperationException("Docker is not ready. Start Docker Desktop/Engine and check docker info.");
                var dockerEnv = new Dictionary<string, string?>(env)
                {
                    ["MSSQL_SA_PASSWORD"] = connection.Password,
                    ["SAW_TEST_SQL_PORT"] = address[1],
                    ["SAW_TEST_SQL_CPUS"] = Math.Clamp(dockerCpus, 1, 4).ToString()
                };
                await Processes.Checked("docker", ["compose", "-f", "compose.test.yml", "up", "--detach", "--wait", "--wait-timeout", "180"], root.FullName, dockerEnv);
            }
            await SqlProbe(connection);
            if (browser) await BrowserDependencies(root.FullName, env, install: true);
            Console.WriteLine("Setup complete. Fixtures will create their schema/data and drop only their own GUID-named databases.");
            return 0;
        }
        if (command != "test") throw new ArgumentException($"Unknown command {command}.");

        env["SAW_TEST_BROWSER"] = browser ? "1" : "0";
        env["SAW_TEST_REAL_CLOUDINARY"] = profile == "all" ? "1" : "0";
        if (profile == "unit") env["SAW_TEST_SQL_CONNECTION"] = null;
        else await SqlProbe(Connection(env));
        if (browser) await BrowserDependencies(root.FullName, env, install: false);
        if (profile == "all")
            foreach (var key in new[] { "SAW_TEST_CLOUD_NAME", "SAW_TEST_CLOUD_KEY", "SAW_TEST_CLOUD_SECRET" }) TestConfig.Required(env, key);
        var filter = profile switch
        {
            "unit" => "RequiresSql!=true",
            "integration" => "RequiresBrowser!=true&RequiresCloudinary!=true",
            "browser" => "RequiresCloudinary!=true",
            _ => ""
        };
        if (options.TryGetValue("--filter", out var extra)) filter = filter.Length == 0 ? extra : $"({filter})&({extra})";
        var results = Path.Combine(root.FullName, "test-results", profile, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(results);
        env["SAW_TEST_EVIDENCE_DIRECTORY"] = results;
        var testArgs = new List<string> { "test", Path.Combine(root.FullName, "SAW", "SAW.sln"), "--logger", "console;verbosity=normal",
            "--logger", "trx;LogFileName=all-tests.trx", "--results-directory", results };
        if (filter.Length > 0) testArgs.AddRange(["--filter", filter]);
        Console.WriteLine($"Profile: {profile}. Results: {results}");
        var run = await Processes.Run("dotnet", testArgs, root.FullName, env);
        await File.WriteAllTextAsync(Path.Combine(results, "console.log"), run.Output);
        if (run.Code != 0) return run.Code;
        var trx = Path.Combine(results, "all-tests.trx");
        if (!File.Exists(trx)) throw new InvalidOperationException("Test runner produced no TRX; success cannot be verified.");
        var xml = XDocument.Load(trx);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counters = xml.Descendants(ns + "Counters").Single();
        var total = int.Parse(counters.Attribute("total")!.Value);
        if (total == 0 || counters.Attribute("passed")!.Value != total.ToString() || counters.Attribute("notExecuted")!.Value != "0")
            throw new InvalidOperationException("Selected tests did not all execute and pass. Skipped or zero selected tests are not accepted.");
        Console.WriteLine($"Verified {total} passed, 0 failed, 0 skipped for profile {profile}. TRX: {trx}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(Processes.Redact(error.Message));
        return 1;
    }
}

static SqlConnectionStringBuilder Connection(Dictionary<string, string?> env)
{
    var builder = new SqlConnectionStringBuilder(TestConfig.Required(env, "SAW_TEST_SQL_CONNECTION"));
    if (!string.IsNullOrWhiteSpace(builder.Password)) Processes.Secrets.Add(builder.Password);
    return builder;
}

static async Task SqlProbe(SqlConnectionStringBuilder supplied)
{
    // Never connect to, migrate or seed the configured application catalog.
    var masterOptions = new SqlConnectionStringBuilder(supplied.ConnectionString)
        { InitialCatalog = "master", Pooling = false, ConnectTimeout = 5 };
    await using var master = new SqlConnection(masterOptions.ConnectionString);
    await master.OpenAsync();
    var name = "SAW_Setup_Test_" + Guid.NewGuid().ToString("N");
    var created = false;
    try
    {
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{name}]";
        await create.ExecuteNonQueryAsync(); created = true;
        var options = new SqlConnectionStringBuilder(masterOptions.ConnectionString) { InitialCatalog = name };
        await using var db = new SqlConnection(options.ConnectionString);
        await db.OpenAsync();
        await using var probe = db.CreateCommand();
        probe.CommandText = "CREATE TABLE dbo.TestSetupProbe (Id int NOT NULL PRIMARY KEY); INSERT dbo.TestSetupProbe VALUES (1); SELECT COUNT(*) FROM dbo.TestSetupProbe;";
        if (Convert.ToInt32(await probe.ExecuteScalarAsync()) != 1) throw new InvalidOperationException("SQL setup probe failed.");
        Console.WriteLine($"SQL ready: created {name}, created table, inserted and read test data.");
    }
    finally
    {
        if (created)
        {
            await using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
            Console.WriteLine("SQL setup probe database removed.");
        }
    }
}

static async Task BrowserDependencies(string root, Dictionary<string, string?> env, bool install)
{
    var frontend = Path.GetFullPath(Path.Combine(root, "..", "Capstone_SAW_FE"));
    if (!File.Exists(Path.Combine(frontend, "package.json")))
        throw new InvalidOperationException("Clone Capstone_SAW_FE next to the backend, using that folder name. See docs/Testing.md.");
    var version = await Processes.Run("node", ["--version"], root, env, quiet: true);
    if (version.Code != 0 || !Version.TryParse(version.Output.Trim().TrimStart('v'), out var parsed) || parsed.Major != 22 || parsed.Minor < 22)
        throw new InvalidOperationException("Browser tests require Node >=22.22.0 <23, matching the frontend package.json. Install Node 22 LTS and reopen the terminal.");
    env.TryGetValue("EDGE_PATH", out var browser);
    if (string.IsNullOrWhiteSpace(browser)) browser = FindBrowser();
    if (browser is null || !File.Exists(browser)) throw new InvalidOperationException("Chrome/Edge not found. Install a Chromium browser or set EDGE_PATH to its executable in .env.test.local.");
    env["EDGE_PATH"] = browser;
    Console.WriteLine($"Browser: {browser}");
    if (install) await Processes.Checked("npm", ["ci"], frontend, env);
    else if (!Directory.Exists(Path.Combine(frontend, "node_modules", "vite")))
        throw new InvalidOperationException("Frontend dependencies missing. Run setup with --profile browser, or npm ci in Capstone_SAW_FE.");
}

static string? FindBrowser()
{
    var candidates = new List<string>();
    if (OperatingSystem.IsMacOS()) candidates.AddRange(["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"]);
    if (OperatingSystem.IsWindows())
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (var app in new[] { Path.Combine("Google", "Chrome", "Application", "chrome.exe"), Path.Combine("Microsoft", "Edge", "Application", "msedge.exe") }) candidates.Add(Path.Combine(folder, app));
    foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var app in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" }) candidates.Add(Path.Combine(folder, app));
    return candidates.FirstOrDefault(File.Exists);
}
