#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetPath = Join-Path $repoRoot '.env.container.local'

if (Test-Path -LiteralPath $targetPath) {
    Write-Host 'Kept existing .env.container.local; no credentials were changed.'
    return
}

$templatePath = Join-Path $repoRoot '.env.container.example'
$jwtBytes = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($jwtBytes) }
finally { $generator.Dispose() }
$jwtKey = [BitConverter]::ToString($jwtBytes).Replace('-', '')
$content = [IO.File]::ReadAllText($templatePath).Replace('REPLACE_WITH_A_RANDOM_JWT_KEY', $jwtKey)
[IO.File]::WriteAllText($targetPath, $content, [Text.UTF8Encoding]::new($false))
Write-Host 'Created .env.container.local with a random JWT key. Configure the SQL connection before checking readiness. Do not commit this file.'
