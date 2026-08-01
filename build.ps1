$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

# ============================================================================
# Local build: publishes the (Avalonia) app self-contained to build\desktop.
# The legacy WinForms/WebView2 host and its WebUI are gone — no Node required.
# ============================================================================

# Toolchain bootstrap: this machine keeps the .NET SDK in ~\.dotnet; a dotnet.exe on
# PATH is not enough (C:\Program Files\dotnet can be a runtime-only install), so
# probe for an actual SDK.
$hasSdk = $false
try {
  $sdks = & dotnet --list-sdks 2>$null
  $hasSdk = ($LASTEXITCODE -eq 0) -and $sdks
} catch { $hasSdk = $false }
if (-not $hasSdk) {
  $userDotnet = Join-Path $env:USERPROFILE '.dotnet'
  if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
    $env:Path = "$userDotnet;$env:Path"
    $env:DOTNET_ROOT = $userDotnet
  }
}

$buildRoot = Join-Path $PSScriptRoot 'build'
$outDir = Join-Path $buildRoot 'desktop'
$project = Join-Path $PSScriptRoot 'AudioMatrixRouter.App\AudioMatrixRouter.App.csproj'

Write-Host 'Stopping running app processes...'
Get-Process AudioMatrixRouter.App, AudioMatrixRouter -ErrorAction SilentlyContinue |
  Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'Cleaning build output...'
if (Test-Path $buildRoot) {
  Remove-Item $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# Version from the repo-root VERSION file (leading "v" stripped) so the assembly and
# the in-app updater agree with the GitHub release tag.
$appVersion = (Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim().TrimStart('v')
Write-Host "App version: $appVersion"

Write-Host 'Publishing app...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $outDir -p:PublishTrimmed=false -p:Platform=x64 -p:Version=$appVersion -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
  Write-Host 'ERROR: dotnet publish failed.'
  exit 1
}

@('Release', 'Debug', 'x64') | ForEach-Object {
  $nested = Join-Path $outDir $_
  if (Test-Path $nested) {
    Remove-Item $nested -Recurse -Force -ErrorAction SilentlyContinue
  }
}

Write-Host ''
Write-Host 'Build complete.'
$exePath = Join-Path $outDir 'AudioMatrixRouter.App.exe'
if (Test-Path $exePath) {
  Get-Item $exePath | Select-Object FullName, LastWriteTime, Length | Format-List
}

exit 0
