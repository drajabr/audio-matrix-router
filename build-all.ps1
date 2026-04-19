$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$buildRoot = Join-Path $PSScriptRoot 'build'
$webOut = Join-Path $buildRoot 'web'
$desktopOut = Join-Path $buildRoot 'desktop'
$webUiPath = Join-Path $PSScriptRoot 'AudioMatrixRouter\WebUI'
$desktopProject = Join-Path $PSScriptRoot 'AudioMatrixRouter\AudioMatrixRouter.csproj'

Write-Host 'Stopping running desktop processes...'
Get-Process AudioMatrixRouter, msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'Cleaning build output folders...'
if (Test-Path $buildRoot) {
  Remove-Item $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $webOut -Force | Out-Null
New-Item -ItemType Directory -Path $desktopOut -Force | Out-Null

Write-Host 'Building web deliverable (GitHub Pages/base-path mode)...'
Push-Location $webUiPath
npm run build:web
Pop-Location
Copy-Item (Join-Path $webUiPath 'dist\*') $webOut -Recurse -Force

Write-Host 'Building desktop WebUI bundle...'
Push-Location $webUiPath
npm run build:windows
Pop-Location

Write-Host 'Publishing desktop app...'
dotnet clean $desktopProject -c Release
dotnet publish $desktopProject -c Release -r win-x64 --self-contained false -o $desktopOut -p:UseSharedCompilation=false -t:Rebuild

Write-Host 'Removing nested build trees from desktop output...'
@('Release', 'Debug', 'x64') | ForEach-Object {
  $nested = Join-Path $desktopOut $_
  if (Test-Path $nested) {
    Remove-Item $nested -Recurse -Force -ErrorAction SilentlyContinue
  }
}

Write-Host ''
Write-Host 'Build complete.'
Write-Host "Web output     : $webOut"
Write-Host "Desktop output : $desktopOut"
$exePath = Join-Path $desktopOut 'AudioMatrixRouter.exe'
if (Test-Path $exePath) {
  (Get-Item $exePath).LastWriteTime = Get-Date
}
Get-Item $exePath | Select-Object FullName, LastWriteTime, Length | Format-List

Write-Host ''
Write-Host 'Building windows mode for local preview (root base path)...'
Push-Location $webUiPath
npm run build:windows
if ($LASTEXITCODE -ne 0) {
  Write-Host 'ERROR: Windows build failed.'
  Pop-Location
  exit 1
}
Pop-Location

Write-Host ''
Write-Host 'Starting local preview server on http://localhost:4173'
Write-Host 'Press Ctrl+C to stop.'
Write-Host ''
Push-Location $webUiPath
try {
  npm run preview
} finally {
  Pop-Location
}
