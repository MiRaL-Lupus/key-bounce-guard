[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$appProject = Join-Path $root 'KeyBounceGuard.csproj'
$installerProject = Join-Path $root 'installer\KeyBounceGuardSetup.csproj'
$publishDir = Join-Path $root 'artifacts\app'
$payload = Join-Path $root 'installer\payload\KeyBounceGuard.exe'
$installerOut = Join-Path $root 'artifacts\installer'

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $payload) | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir 'KeyBounceGuard.exe') -Destination $payload -Force

Remove-Item -LiteralPath $installerOut -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet publish $installerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $installerOut

$setup = Join-Path $installerOut 'KeyBounceGuard-Setup.exe'
$checksums = Join-Path $installerOut 'SHA256SUMS.txt'
@(
    Get-FileHash -Algorithm SHA256 $setup
    Get-FileHash -Algorithm SHA256 (Join-Path $publishDir 'KeyBounceGuard.exe')
) | ForEach-Object { "{0}  {1}" -f $_.Hash, (Split-Path -Leaf $_.Path) } | Set-Content -LiteralPath $checksums -Encoding ascii
Get-Content -LiteralPath $checksums
Write-Host "Created: $setup"
