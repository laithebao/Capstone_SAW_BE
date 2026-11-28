param([switch]$SkipBuild, [string]$BaselineBackupPath)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$config = Get-Content (Join-Path $repoRoot 'SAW/SAW.API/appsettings.Development.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$source = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($config.ConnectionStrings.DefaultConnection)
if ($source.DataSource -notmatch '^(localhost|\.|127\.0\.0\.1)(\\SQLEXPRESS)?$' -or $source.InitialCatalog -ne 'SmartAgriWarehouseDB') { throw 'This verification only supports the named local development database.' }
$runId = [Guid]::NewGuid().ToString('N')
$cloneName = "SAW_BigUpdate01_$runId"
$admin = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($source.ConnectionString)
$admin['Initial Catalog'] = 'master'
$connection = New-Object System.Data.SqlClient.SqlConnection($admin.ConnectionString)
$clone = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($source.ConnectionString)
$clone['Initial Catalog'] = $cloneName
$created = $false
$previousConnection = $env:ConnectionStrings__DefaultConnection
$stage = 'connect'
$log = Join-Path ([IO.Path]::GetTempPath()) "saw-migration-$runId.log"
function Query([string]$sql) {
    $cmd = $connection.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
    $reader = $cmd.ExecuteReader(); $table = New-Object System.Data.DataTable
    $table.Load($reader); $reader.Close(); return ,$table
}
function Execute([string]$sql) {
    $cmd = $connection.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120; [void]$cmd.ExecuteNonQuery()
}
function Quote([string]$value) { return $value.Replace("'", "''") }
function Migrate([string]$target) {
    $env:ConnectionStrings__DefaultConnection = $clone.ConnectionString
    & dotnet ef database update $target --project "$repoRoot/SAW/SAW.Infrastructure" --startup-project "$repoRoot/SAW/SAW.API" --no-build -- --environment Development *> $log
    if ($LASTEXITCODE -ne 0) { throw 'Isolated EF migration failed; inspect the local log without publishing credentials.' }
}
try {
    if (!$SkipBuild) {
        $stage = 'build'; & dotnet build "$repoRoot/SAW/SAW.API/SAW.API.csproj" --no-restore -v quiet *> $log
        if ($LASTEXITCODE -ne 0) { throw 'Build failed before any database operation.' }
    }
    $connection.Open()
    $paths = (Query "SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath')) AS BackupPath, CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath')) AS DataPath, CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultLogPath')) AS LogPath").Rows[0]
    if ($BaselineBackupPath) {
        $backup = [IO.Path]::GetFullPath($BaselineBackupPath)
        if (!(Test-Path -LiteralPath $backup -PathType Leaf) -or [IO.Path]::GetExtension($backup) -ne '.bak') { throw 'Provide an existing pre-BigUpdate SQL Server .bak file.' }
    } else {
        $backup = Join-Path $paths.BackupPath "SAW_BigUpdate01_$runId.bak"
        $stage = 'backup'
        Execute "BACKUP DATABASE [SmartAgriWarehouseDB] TO DISK=N'$(Quote $backup)' WITH COPY_ONLY,CHECKSUM"
    }
    Execute "RESTORE VERIFYONLY FROM DISK=N'$(Quote $backup)' WITH CHECKSUM"
    Write-Output 'PASS backup checksum verification'
    $files = Query "RESTORE FILELISTONLY FROM DISK=N'$(Quote $backup)'"
    $moves = @(); $index = 0
    foreach($file in $files.Rows) {
        $index++; $directory = if($file.Type -eq 'L') { $paths.LogPath } else { $paths.DataPath }
        $extension = if($file.Type -eq 'L') { 'ldf' } else { 'mdf' }
        $targetPath = Join-Path $directory "${cloneName}_${index}.$extension"
        $moves += "MOVE N'$(Quote $file.LogicalName)' TO N'$(Quote $targetPath)'"
    }
    $stage = 'restore'
    if ((Query "SELECT DB_ID(N'$cloneName') AS Id").Rows[0].Id -isnot [DBNull]) { throw 'Clone target already exists.' }
    Execute "RESTORE DATABASE [$cloneName] FROM DISK=N'$(Quote $backup)' WITH CHECKSUM,$($moves -join ',')"
    $created = $true; Execute "USE [$cloneName]"
    $baseline = (Query 'SELECT MAX(MigrationId) AS Id FROM __EFMigrationsHistory').Rows[0].Id
    if ($baseline -ne '20261112160000_EnsurePickingHistorySync') { throw 'The restored backup must have the 28-migration baseline. Current databases already at 30 need -BaselineBackupPath pointing to the verified pre-update backup; never downgrade the source for verification.' }
    $countsSql = "SELECT (SELECT COUNT(*) FROM ACCOUNT) Accounts,(SELECT COUNT(*) FROM SUPPLIER) Suppliers,(SELECT COUNT(*) FROM PRODUCT_BATCH) Batches,(SELECT COUNT(*) FROM SUPPLY_CONTRACT) Contracts,(SELECT COUNT(*) FROM INVENTORY) Inventories,(SELECT SUM(QuantityOnHand) FROM INVENTORY) Stock,(SELECT COUNT(*) FROM sys.triggers) Triggers"
    Execute 'SELECT CropTypeID,CropCode,CreatedAt INTO #OriginalCrops FROM CROP_TYPE;'
    $before = (Query $countsSql).Rows[0]
    $stage = 'up'; Migrate '20261125163000_NormalizeLegacyCropCodes'
    $after = (Query $countsSql).Rows[0]
    foreach($column in $before.Table.Columns.ColumnName) { if ($before[$column].ToString() -ne $after[$column].ToString()) { throw "Unexpected business data change: $column" } }
    Execute @'
IF (SELECT COUNT(*) FROM CROP_CODE_MIGRATION_01)<>(SELECT COUNT(*) FROM #OriginalCrops WHERE CropCode IN ('RICE-ST25','POTATO-DL','CARROT-DL','DEMO-DIST-RICE','DEMO-DIST-MANGO','DEMO-DIST-DRAGON')) THROW 51120,'Only existing approved legacy rows may be mapped.',1;
IF EXISTS(SELECT 1 FROM CROP_TYPE c JOIN CROP_CODE_MIGRATION_01 m ON c.CropTypeID=m.CropTypeId WHERE c.CreatedAt<>m.OriginalCreatedAt OR c.CropCode<>m.NewCode OR c.CategoryId IS NULL) THROW 51121,'Mapping invariant failed.',1;
IF (SELECT COUNT(*) FROM CROP_TYPE)<>(SELECT COUNT(*) FROM #OriginalCrops) OR EXISTS(SELECT 1 FROM #OriginalCrops o LEFT JOIN CROP_TYPE c ON c.CropTypeID=o.CropTypeID WHERE c.CropTypeID IS NULL OR c.CreatedAt<>o.CreatedAt OR (NOT EXISTS(SELECT 1 FROM CROP_CODE_MIGRATION_01 m WHERE m.CropTypeId=o.CropTypeID) AND c.CropCode<>o.CropCode)) THROW 51122,'Unexpected creation, identity/date change or modification of an unmapped crop.',1;
IF EXISTS(SELECT 1 FROM EMAIL_OUTBOX) OR EXISTS(SELECT 1 FROM SUPPLY_CONTRACT_REVISION) OR EXISTS(SELECT 1 FROM sys.triggers WHERE is_disabled=1) THROW 51124,'Unexpected outbox, revision or disabled trigger.',1;
'@
    Write-Output 'PASS isolated Up, dynamic existing mappings, no invented crops or email, CreatedAt/IDs and business data preservation'
    $stage = 'down'; Migrate $baseline
    Execute "IF (SELECT COUNT(*) FROM CROP_TYPE)<>(SELECT COUNT(*) FROM #OriginalCrops) OR EXISTS(SELECT 1 FROM #OriginalCrops o LEFT JOIN CROP_TYPE c ON c.CropTypeID=o.CropTypeID WHERE c.CropTypeID IS NULL OR c.CropCode<>o.CropCode OR c.CreatedAt<>o.CreatedAt) THROW 51123,'Rollback mapping failed.',1;"
    Write-Output 'PASS isolated Down restores the exact existing original codes and dates'
    $stage = 'reapply'; Migrate '20261125163000_NormalizeLegacyCropCodes'
    Write-Output 'PASS isolated reapply'
    Query 'SELECT OldCode,NewCode,DateRule FROM CROP_CODE_MIGRATION_01 ORDER BY CropTypeId' | Format-Table -AutoSize
    Write-Output "Verified backup retained at: $backup"
}
catch { Write-Error "Verification failed at stage '$stage' ($($_.Exception.GetType().Name)). No source database migration was attempted." }
finally {
    $env:ConnectionStrings__DefaultConnection = $previousConnection
    if ($created -and $cloneName -match '^SAW_BigUpdate01_[0-9a-f]{32}$' -and $cloneName -ne $source.InitialCatalog) {
        try { Execute "USE [master]; ALTER DATABASE [$cloneName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$cloneName]" } catch { Write-Warning 'The isolated clone could not be removed.' }
    }
    $connection.Dispose()
}
