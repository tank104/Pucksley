# Installs the Pucksley hub for this Windows user, in %LOCALAPPDATA%\Programs\Pucksley, with a Start
# menu entry. Running it again updates: it stops the running hub, swaps the new copy in and starts it
# again. Settings (%APPDATA%\Pucksley) aren't touched.
#
#   irm https://raw.githubusercontent.com/tank104/Pucksley/main/scripts/install.ps1 | iex
#                                        # the latest release, downloaded from GitHub
#   .\install.ps1 -From dist\Pucksley    # a build of your own
param([string]$From)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower drawing its bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'tank104/Pucksley'
$home_ = Join-Path $env:LOCALAPPDATA 'Programs\Pucksley'
$download = $null

try {
  if (-not $From) {
    Write-Host '== downloading the latest release' -ForegroundColor Cyan
    $download = Join-Path ([IO.Path]::GetTempPath()) ('pucksley-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $download | Out-Null
    $zip = Join-Path $download 'Pucksley-windows-x64.zip'
    Invoke-WebRequest -UseBasicParsing "https://github.com/$repo/releases/latest/download/Pucksley-windows-x64.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath (Join-Path $download 'unpacked')
    $From = Join-Path $download 'unpacked\Pucksley'
  }
  $From = (Resolve-Path $From).Path
  if (-not (Test-Path (Join-Path $From 'Pucksley.exe'))) { throw "There's no Pucksley.exe in $From." }

  Write-Host "== installing to $home_" -ForegroundColor Cyan
  $running = Get-Process -Name Pucksley -ErrorAction SilentlyContinue
  if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
  }

  # Copy beside it, then swap, so a half-copied hub never runs
  $new = "$home_.new"; $old = "$home_.old"
  foreach ($dir in $new, $old) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
  New-Item -ItemType Directory -Path $new | Out-Null
  Copy-Item -Path (Join-Path $From '*') -Destination $new -Recurse -Force
  if (Test-Path $home_) { Rename-Item $home_ $old }
  Rename-Item $new $home_
  if (Test-Path $old) { Remove-Item $old -Recurse -Force }
  $exe = Join-Path $home_ 'Pucksley.exe'

  # A Start menu entry, and Start with Windows (when it's on) pointing at this copy
  $shell = New-Object -ComObject WScript.Shell
  $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Pucksley.lnk'))
  $link.TargetPath = $exe
  $link.WorkingDirectory = $home_
  $link.Description = 'The hub behind the Pucksley voice assistant puck'
  $link.Save()
  $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
  if (Get-ItemProperty -Path $run -Name 'Pucksley' -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $run -Name 'Pucksley' -Value "`"$exe`""
  }

  Start-Process -FilePath $exe -WorkingDirectory $home_
  Write-Host '   Pucksley is running: look for its tray icon. Its page is http://127.0.0.1:8765'
  if (-not $running) { Write-Host '   To start it with Windows, tick Start with Windows in its tray menu.' }
}
finally {
  if ($download -and (Test-Path $download)) { Remove-Item $download -Recurse -Force -ErrorAction SilentlyContinue }
}
