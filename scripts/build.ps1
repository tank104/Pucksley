# Builds everything into dist/:
#   dist/Pucksley/           the Windows hub (tray app, self-contained: no .NET install needed)
#   dist/firmware/           the puck firmware images (flash with idf.py or esptool)
#
#   ./scripts/build.ps1                 # both
#   ./scripts/build.ps1 -HubOnly
#   ./scripts/build.ps1 -FirmwareOnly
#
# Needs: the .NET 10 SDK, and ESP-IDF 5.5 (default location %USERPROFILE%\esp\esp-idf).
param(
  [switch]$HubOnly,
  [switch]$FirmwareOnly,
  [string]$IdfPath = "$env:USERPROFILE\esp\esp-idf"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $FirmwareOnly) {
  Write-Host '== hub (Windows tray app)' -ForegroundColor Cyan
  $out = Join-Path $dist 'Pucksley'
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  dotnet publish (Join-Path $root 'hub/src/Hosts/Pucksley.Tray/Pucksley.Tray.csproj') -c Release -r win-x64 --self-contained -o $out
  if ($LASTEXITCODE -ne 0) { throw 'hub build failed' }
  Write-Host "   -> $out\Pucksley.exe"
}

if (-not $HubOnly) {
  Write-Host '== firmware (ESP-IDF)' -ForegroundColor Cyan
  # Started from Git Bash? The IDF tools refuse MSYS environments.
  foreach ($v in 'MSYSTEM', 'MSYSTEM_PREFIX', 'MSYSTEM_CHOST', 'MINGW_PREFIX', 'MINGW_CHOST') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
  $env:IDF_PATH = $IdfPath
  # ESP-IDF's export and idf.py print progress on stderr, which 'Stop' would treat as a failure
  $ErrorActionPreference = 'Continue'
  . (Join-Path $IdfPath 'export.ps1') | Out-Null
  if (-not (Get-Command idf.py -ErrorAction SilentlyContinue)) { throw "ESP-IDF didn't load from $IdfPath (pass -IdfPath)" }
  Push-Location (Join-Path $root 'firmware')
  try {
    idf.py build
    if ($LASTEXITCODE -ne 0) { throw 'firmware build failed' }
    $fw = Join-Path $dist 'firmware'
    New-Item -ItemType Directory -Force $fw | Out-Null
    Copy-Item build/bootloader/bootloader.bin, build/partition_table/partition-table.bin, build/ota_data_initial.bin, build/pucksley.bin, build/flash_args $fw -Force
    Write-Host "   -> $fw (flash: idf.py -p COMx flash, or esptool with flash_args)"
  }
  finally { Pop-Location }
}
