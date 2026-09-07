$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Get-IslandDotNet {
    $candidates = @((Join-Path $projectRoot '.build\dotnet\dotnet.exe'))
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { $candidates += $installed.Source }
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate) -and ((& $candidate --list-sdks) -match '^10\.')) {
            return $candidate
        }
    }
    throw 'A .NET 10 SDK is required. Run scripts\setup-windows.ps1 or install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}
