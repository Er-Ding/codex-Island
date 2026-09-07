param([switch]$IncludeUI, [switch]$LiveQuota)
. (Join-Path $PSScriptRoot 'windows-common.ps1')
$dotnet = Get-IslandDotNet
& $dotnet build (Join-Path $projectRoot 'CodexIsland.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
& $dotnet (Join-Path $projectRoot 'CodexIsland.Checks\bin\Release\net10.0\CodexIsland.Checks.dll')
if ($LASTEXITCODE -ne 0) { throw 'Core or transport checks failed.' }
$app = Join-Path $projectRoot 'CodexIsland.Windows\bin\Release\net10.0-windows\CodexIsland.dll'
if ($IncludeUI) {
    $outputDirectory = Join-Path $projectRoot ('.build\ui-' + [Guid]::NewGuid().ToString('N'))
    & $dotnet $app --smoke-test $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw "UI checks failed. See $outputDirectory\failure.txt" }
}
if ($LiveQuota) {
    & $dotnet $app --check-quota
    if ($LASTEXITCODE -ne 0) { throw 'Live quota check failed. Check Windows Codex login and connectivity.' }
}
