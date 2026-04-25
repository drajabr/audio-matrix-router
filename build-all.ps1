$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$buildRoot = Join-Path $PSScriptRoot 'build'
$webOut = Join-Path $buildRoot 'web'
$desktopOut = Join-Path $buildRoot 'desktop'
$webUiPath = Join-Path $PSScriptRoot 'AudioMatrixRouter\WebUI'
$desktopProject = Join-Path $PSScriptRoot 'AudioMatrixRouter\AudioMatrixRouter.csproj'
$desktopConfigPath = Join-Path $desktopOut 'config.json'
$preservedConfig = $null

Write-Host 'Stopping running desktop processes...'
$appPids = @((Get-Process AudioMatrixRouter -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id))
$processSnapshot = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
$descendantPids = @()

if ($appPids.Count -gt 0 -and $processSnapshot.Count -gt 0) {
  $queue = New-Object System.Collections.Generic.Queue[int]
  foreach ($appPid in $appPids) {
    $queue.Enqueue([int]$appPid)
  }

  while ($queue.Count -gt 0) {
    $currentPid = $queue.Dequeue()
    $children = $processSnapshot | Where-Object { $_.ParentProcessId -eq $currentPid }
    foreach ($child in $children) {
      $childPid = [int]$child.ProcessId
      if ($descendantPids -notcontains $childPid) {
        $descendantPids += $childPid
        $queue.Enqueue($childPid)
      }
    }
  }
}

$webViewChildPids = @($processSnapshot | Where-Object { $_.Name -eq 'msedgewebview2.exe' -and ($descendantPids -contains [int]$_.ProcessId) } | Select-Object -ExpandProperty ProcessId)

$pidsToStop = @($appPids + $webViewChildPids | Sort-Object -Unique)
if ($pidsToStop.Count -gt 0) {
  Stop-Process -Id $pidsToStop -Force -ErrorAction SilentlyContinue
}

if (Test-Path $desktopConfigPath) {
  try {
    $preservedConfig = Get-Content $desktopConfigPath -Raw
    Write-Host 'Preserved existing desktop config.json'
  } catch {
    Write-Host 'Warning: Could not preserve existing config.json; continuing with clean build.'
  }
}

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

if ($null -ne $preservedConfig -and -not (Test-Path $desktopConfigPath)) {
  try {
    Set-Content -Path $desktopConfigPath -Value $preservedConfig -Encoding UTF8
    Write-Host 'Restored preserved desktop config.json'
  } catch {
    Write-Host 'Warning: Failed to restore preserved config.json.'
  }
}

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
  try {
    (Get-Item $exePath).LastWriteTime = Get-Date
  } catch {
    Write-Host 'Warning: Could not update desktop exe timestamp; continuing.'
  }
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
Write-Host 'Preview server will run in the background.'
Write-Host ''

$npmCmdPath = (Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
if (-not $npmCmdPath) {
  Write-Host 'Warning: npm.cmd not found on PATH; skipping preview server startup.'
  exit 0
}

$previewCommand = '"' + $npmCmdPath + '" run preview -- --host 127.0.0.1 --port 4173'
try {
  $previewProcess = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $previewCommand) -WorkingDirectory $webUiPath -WindowStyle Hidden -PassThru
  Write-Host "Preview process started (PID: $($previewProcess.Id))."
}
catch {
  Write-Host "Warning: Failed to start preview server in background: $($_.Exception.Message)"
}

exit 0
