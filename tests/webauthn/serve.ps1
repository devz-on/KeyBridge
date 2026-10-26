param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'server/KeyBridge.WebAuthn.Server.csproj'
& $Dotnet run --project $project -c Release -- $PSScriptRoot
exit $LASTEXITCODE
