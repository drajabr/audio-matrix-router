$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$buildRoot = Join-Path $PSScriptRoot 'build'
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
  Write-Host ("Stopping stale processes: {0}" -f ($pidsToStop -join ', '))
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
New-Item -ItemType Directory -Path $desktopOut -Force | Out-Null

Write-Host 'Building desktop WebUI bundle...'
Push-Location $webUiPath
npm run build
if ($LASTEXITCODE -ne 0) {
  Pop-Location
  Write-Host 'ERROR: WebUI build failed.'
  exit 1
}
Pop-Location

Write-Host 'Publishing desktop app...'
# Version comes from the repo-root VERSION file (leading "v" stripped) so the assembly
# and the in-app updater agree with the GitHub release tag.
$appVersion = (Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim().TrimStart('v')
Write-Host "App version: $appVersion"
dotnet clean $desktopProject -c Release
# NOTE: PublishSingleFile removed — Velopack packages the whole publish directory and
# recommends a plain multi-file self-contained publish.
dotnet publish $desktopProject -c Release -r win-x64 --self-contained true -o $desktopOut -p:PublishTrimmed=false -p:Platform=x64 -p:UseSharedCompilation=false -p:Version=$appVersion -t:Rebuild
if ($LASTEXITCODE -ne 0) {
  Write-Host 'ERROR: dotnet publish failed.'
  exit 1
}

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
Write-Host "Desktop output : $desktopOut"
$exePath = Join-Path $desktopOut 'AudioMatrixRouter.exe'
if (Test-Path $exePath) {
  try {
    (Get-Item $exePath).LastWriteTime = Get-Date
  } catch {
    Write-Host 'Warning: Could not update desktop exe timestamp; continuing.'
  }
  Get-Item $exePath | Select-Object FullName, LastWriteTime, Length | Format-List
}

exit 0
