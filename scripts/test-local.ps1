$ErrorActionPreference = 'Stop'
$backendDir = Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $backendDir 'tools/SAW.Testing') -- test --profile all @args
exit $LASTEXITCODE
