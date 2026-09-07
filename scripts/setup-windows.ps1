# Installs a project-local SDK. Does not change system PATH or require elevation.
. (Join-Path $PSScriptRoot 'windows-common.ps1')
$buildDirectory = Join-Path $projectRoot '.build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$installer = Join-Path $buildDirectory 'dotnet-install.ps1'
Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Channel 10.0 -Quality GA -InstallDir (Join-Path $buildDirectory 'dotnet') -NoPath
$sdk = Join-Path $buildDirectory 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk) -or !((& $sdk --list-sdks) -match '^10\.')) { throw '.NET SDK installation failed.' }
Write-Output 'The SDK is ready. Run scripts\build-windows.ps1 next.'
