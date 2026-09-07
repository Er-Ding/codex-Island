param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)
. (Join-Path $PSScriptRoot 'windows-common.ps1')
[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$releaseVersion = [string]$versionProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($releaseVersion)) { throw 'Windows release Version is missing from Directory.Build.props.' }
$dotnet = Get-IslandDotNet
$flavor = if ($FrameworkDependent) { "$Runtime-framework" } else { $Runtime }
$outputDirectory = Join-Path $projectRoot "dist\$flavor"
$project = Join-Path $projectRoot 'CodexIsland.Windows\CodexIsland.Windows.csproj'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $dotnet publish $project -c Release -r $Runtime --self-contained $selfContained -o $outputDirectory `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed. Close any running copy in the output folder before rebuilding.' }
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $outputDirectory 'README.md')
# Keep the user guide's relative links and previews usable in an extracted portable package.
$guideDirectory = Join-Path $outputDirectory 'windows'
New-Item -ItemType Directory -Path $guideDirectory -Force | Out-Null
foreach ($folder in @('docs', 'assets')) {
    $destination = Join-Path $guideDirectory $folder
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
$archive = Join-Path $projectRoot "dist\Codex-Island-$releaseVersion-$flavor.zip"
Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`n")
Write-Output "Application: $(Join-Path $outputDirectory 'CodexIsland.exe')"
Write-Output "Archive: $archive"
