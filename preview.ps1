$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$webUiPath = Join-Path $PSScriptRoot 'AudioMatrixRouter\WebUI'

Write-Host 'Building windows mode (local preview with root base path)...'
Write-Host ''

Push-Location $webUiPath

# Build windows mode (./base path instead of /audio-matrix-router/)
npm run build:windows

if ($LASTEXITCODE -ne 0) {
  Write-Host 'ERROR: Build failed.'
  Pop-Location
  exit 1
}

Write-Host ''
Write-Host 'Starting Vite preview server on http://localhost:4173'
Write-Host 'Press Ctrl+C to stop.'
Write-Host ''

npm run preview

Pop-Location
