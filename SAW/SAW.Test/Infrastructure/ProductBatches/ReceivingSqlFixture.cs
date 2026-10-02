using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.ProductBatches;

// Opt in with SAW_TEST_SQL_CONNECTION. Only the server/login are used: every run creates
// and drops its own GUID-named DB. Never migrate, seed or delete rows in the supplied DB.
public sealed class ReceivingSqlFactAttribute : FactAttribute
{
    public ReceivingSqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION")))
            Skip = "Set SAW_TEST_SQL_CONNECTION to run isolated SQL Server tests.";
    }
}

public sealed class ReceivingSqlFixture : IAsyncLifetime
{
    private readonly string _database = "SAW_UC28_Test_" + Guid.NewGuid().ToString("N");
    private string _master = "";
    private bool _created;
    private Process? _api;
    private Uri? _apiAddress;
    public string ConnectionString { get; private set; } = "";
    public int ActorId { get; private set; }
    public int SupplierId { get; private set; }
    public int CropTypeId { get; private set; }
    public int AreaId { get; private set; }
    public long VersionId { get; private set; }
    public const string JwtKey = "UC28-isolated-integration-test-key-only-123456789";

    public AppDbContext Context(DbCommandInterceptor? interceptor = null, bool retry = true)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString,
            sql => { if (retry) sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null); });
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options);
    }

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable("SAW_TEST_SQL_CONNECTION");
        if (string.IsNullOrEmpty(supplied)) return;
        var builder = new SqlConnectionStringBuilder(supplied) { InitialCatalog = "master" };
        _master = builder.ConnectionString;
        await using var master = new SqlConnection(_master);
        await master.OpenAsync();
        await ExecuteAsync(master, $"CREATE DATABASE [{_database}]");
        _created = true;
        builder.InitialCatalog = _database;
        ConnectionString = builder.ConnectionString;
        try
        {
            await using var db = Context();
            // EF's full model contains multiple cascade paths. This isolated schema retains
            // every FK but uses NO ACTION on delete; these tests never delete entities.
            var script = db.Database.GenerateCreateScript().Replace("ON DELETE CASCADE", "ON DELETE NO ACTION");
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            foreach (var command in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline))
                if (!string.IsNullOrWhiteSpace(command)) await ExecuteAsync(connection, command);
            // Match the dev DB's timestamp precision and existing QC batch/date index.
            await ExecuteAsync(connection, """
                ALTER TABLE PRODUCT_BATCH ALTER COLUMN CreatedAt datetime2(0) NOT NULL;
                ALTER TABLE PRODUCT_BATCH ALTER COLUMN UpdatedAt datetime2(0) NULL;
                CREATE INDEX IX_QC_INSPECTION_Batch_Date ON QC_INSPECTION(ProductBatchID, StartedAt);
                """);
            var role = new Role { RoleCode = "UC28_TEST", RoleName = "Test", IsActive = true };
            var account = new Account { Role = role, Username = "uc28-test", Email = "uc28@test.invalid",
                FullName = "UC28 Test", PasswordHash = "unused-test-account", AccountStatus = "ACTIVE" };
            var supplier = new Supplier { Account = account, SupplierCode = "UC28", SupplierName = "Test supplier",
                TaxCode = "TEST", ContactPerson = "Test", Address = "Test" };
            var crop = new CropType { CropCode = "UC28", CropName = "Test crop", CategoryName = "Test", IsActive = true };
            var area = new GrowingArea { AreaName = "Test", Region = "Test", Province = "Test", District = "Test", Ward = "Test" };
            var version = new InspectionStandardVersion { VersionNo = 1, VersionStatus = "PUBLISHED",
                InspectionStandardSet = new InspectionStandardSet { CropType = crop, StandardCode = "UC28",
                    StandardName = "Test standard", IsActive = true } };
            db.AddRange(supplier, area, version);
            await db.SaveChangesAsync();
            ActorId = account.AccountId; SupplierId = supplier.SupplierId;
            CropTypeId = crop.CropTypeId; AreaId = area.GrowingAreaId;
            VersionId = version.InspectionStandardVersionId;
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<ProductBatch> BatchAsync()
    {
        await using var db = Context();
        var batch = new ProductBatch { BatchCode = "UC28-" + Guid.NewGuid().ToString("N")[..16],
            SupplierId = SupplierId, CropTypeId = CropTypeId, GrowingAreaId = AreaId,
            ProductName = "Test batch", HarvestDate = new DateOnly(2026, 1, 1),
            DeclaredQuantity = 10, WeightInKg = 100, Unit = "Box", PackagingType = "Supplier box",
            PackageCount = 10, PackageUnitWeightKg = 10, Note = "Supplier note",
            VerifiedQuantity = 10, VerifiedWeightInKg = 100, VerifiedPackagingType = "Received box",
            VerifiedPackageCount = 10, VerifiedPackageUnitWeightKg = 10, ReceivingNote = "Received",
            BatchStatus = "PENDING_QC", CreatedAt = new DateTime(2026, 1, 2), UpdatedAt = new DateTime(2026, 1, 3) };
        db.ProductBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch;
    }

    public QcInspection Inspection(long batchId, string status = "DRAFT") => new()
    {
        ProductBatchId = batchId, InspectionCode = "TEST-" + Guid.NewGuid().ToString("N"),
        InspectionStandardVersionId = VersionId, QcAccountId = ActorId,
        InspectionStatus = status, StartedAt = DateTime.UtcNow
    };

    public async Task<int> SessionIdAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT @@SPID";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task AssertBlockedAsync(int waiter, int blocker)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT blocking_session_id FROM sys.dm_exec_requests WHERE session_id=@id";
            command.Parameters.AddWithValue("@id", waiter);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == blocker) return;
            await Task.Delay(50);
        }
        Assert.Fail($"SQL session {waiter} did not block on session {blocker}.");
    }

    public async Task<HttpClient> ApiAsync()
    {
        if (_api is { HasExited: false } && _apiAddress is not null)
            return new HttpClient { BaseAddress = _apiAddress, Timeout = TimeSpan.FromSeconds(20) };
        var solution = new DirectoryInfo(AppContext.BaseDirectory);
        while (solution is not null && !File.Exists(Path.Combine(solution.FullName, "SAW.sln"))) solution = solution.Parent;
        Assert.NotNull(solution);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var apiDir = Path.Combine(solution.FullName, "SAW.API");
        var dll = Path.Combine(apiDir, "bin", configuration, "net8.0", "SAW.API.dll");
        Assert.True(File.Exists(dll), "Build the whole solution before running HTTP tests.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = apiDir, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = ConnectionString;
        start.Environment["QrCode__Enabled"] = "false"; // HTTP read tests must never upload real assets.
        start.Environment["Jwt__Key"] = JwtKey;
        start.Environment["Jwt__Issuer"] = "uc28-test";
        start.Environment["Jwt__Audience"] = "uc28-test";
        _api = Process.Start(start)!;
        _api.BeginOutputReadLine(); _api.BeginErrorReadLine();
        _apiAddress = new Uri($"http://127.0.0.1:{port}");
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        for (var i = 0; i < 100; i++)
        {
            if (_api.HasExited) throw new InvalidOperationException("Isolated API failed to start.");
            try { using var response = await client.GetAsync("/api/operation/product-batches/1"); return client; }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        throw new TimeoutException("Isolated API did not start.");
    }

    public async Task DisposeAsync()
    {
        if (_api is { HasExited: false }) { _api.Kill(entireProcessTree: true); await _api.WaitForExitAsync(); }
        _api?.Dispose();
        if (!_created) return;
        // Only this fixture's GUID-named database is eligible for cleanup.
        if (!Regex.IsMatch(_database, "^SAW_UC28_Test_[a-f0-9]{32}$")) throw new InvalidOperationException();
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_master);
        await master.OpenAsync();
        await ExecuteAsync(master, $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]");
        _created = false;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql; command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
