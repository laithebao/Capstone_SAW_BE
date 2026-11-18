#Requires -Version 5.1
param([string]$Image = 'saw-api:step2')

$ErrorActionPreference = 'Stop'
$suffix = [Guid]::NewGuid().ToString('N')
$containerName = "saw-smoke-$suffix"
$volumeName = "saw-smoke-files-$suffix"
$envPath = Join-Path ([IO.Path]::GetTempPath()) "saw-smoke-$suffix.env"
$containerCreated = $false
$volumeCreated = $false

function Invoke-Docker {
    param([string[]]$Arguments)
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command '$($Arguments[0])' failed." }
    return $output
}

function Start-SmokeContainer {
    $null = Invoke-Docker -Arguments @('run', '--detach', '--name', $containerName,
        '--publish', '127.0.0.1::8080', '--env-file', $envPath,
        '--mount', "type=volume,source=$volumeName,target=/app/App_Data", $Image)
    $script:containerCreated = $true
    $binding = Invoke-Docker -Arguments @('port', $containerName, '8080/tcp')
    $localPort = ($binding.Trim() -split ':')[-1]
    $script:baseUri = "http://127.0.0.1:$localPort"

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-RestMethod "$baseUri/health/live" -TimeoutSec 2
            if ($response.status -eq 'Healthy') { return $response }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    throw 'Container liveness did not become healthy.'
}

try {
    $null = Invoke-Docker -Arguments @('info', '--format', '{{.OSType}}')
    $runtimeUser = Invoke-Docker -Arguments @('image', 'inspect', '--format', '{{.Config.User}}', $Image)
    if ($runtimeUser.Trim() -ne 'app') { throw 'Image must run as the app user.' }

    # Use disposable configuration; never read local SQL/Cloudinary/OpenAI credentials.
    $jwtBytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($jwtBytes) }
    finally { $generator.Dispose() }
    $jwtKey = [BitConverter]::ToString($jwtBytes).Replace('-', '')
    [IO.File]::WriteAllLines($envPath, @(
        "Jwt__Key=$jwtKey",
        'Jwt__Issuer=saw-smoke', 'Jwt__Audience=saw-smoke',
        'ConnectionStrings__DefaultConnection=Server=127.0.0.1,1;Database=saw_smoke;User Id=smoke;Password=unused;Encrypt=False;Connect Timeout=1;ConnectRetryCount=0',
        'QrCode__Enabled=false', 'OpenAI__ApiKey=', 'Qdrant__Endpoint=', 'Qdrant__ApiKey=',
        'Email__DeliveryMode=Log'
    ), [Text.UTF8Encoding]::new($false))
    $null = Invoke-Docker -Arguments @('volume', 'create', $volumeName)
    $volumeCreated = $true
    $live = Start-SmokeContainer

    try {
        $ready = Invoke-WebRequest "$baseUri/health/ready" -TimeoutSec 10 -UseBasicParsing
        $readyStatus = [int]$ready.StatusCode
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $readyStatus = [int]$_.Exception.Response.StatusCode
        $_.Exception.Response.Dispose()
    }
    if ($readyStatus -ne 503) { throw 'Readiness must be 503 for the deliberately unreachable SQL server.' }

    $userId = Invoke-Docker -Arguments @('exec', $containerName, 'id', '-u')
    if ($userId.Trim() -eq '0') { throw 'Container unexpectedly runs as root.' }
    $null = Invoke-Docker -Arguments @('exec', $containerName, 'sh', '-c',
        'test -d /app/KnowledgeBase && test -w /app/App_Data && test ! -f /app/appsettings.json && test ! -f /app/appsettings.Development.json && printf persisted > /app/App_Data/smoke-marker')
    $nativeDependencies = Invoke-Docker -Arguments @('exec', $containerName, 'sh', '-c',
        'ldd /app/runtimes/linux-x64/native/libSkiaSharp.so')
    if ($nativeDependencies -match 'not found') { throw 'A SkiaSharp native dependency is missing.' }

    $null = Invoke-Docker -Arguments @('rm', '--force', $containerName)
    $containerCreated = $false
    $null = Start-SmokeContainer
    $marker = Invoke-Docker -Arguments @('exec', $containerName, 'cat', '/app/App_Data/smoke-marker')
    if ($marker.Trim() -ne 'persisted') { throw 'Files did not survive container replacement.' }

    Write-Host "PASS: Linux container, non-root user, live=200, ready=503 with unavailable SQL, KnowledgeBase, native dependencies and persistent volume. Release: $($live.release)"
}
finally {
    if ($containerCreated) { $null = & docker rm --force $containerName }
    if ($volumeCreated) { $null = & docker volume rm $volumeName }
    if (Test-Path -LiteralPath $envPath) { Remove-Item -LiteralPath $envPath }
}
