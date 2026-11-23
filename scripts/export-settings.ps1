# Packs this PC's Pucksley settings into a zip for another computer (a Linux PC, say), keys and
# puck pairings included. Windows encrypts the hub's secrets so only this Windows user can read
# them; this turns them back into a form any computer can read, and the hub there encrypts them
# again with its own key the first time it starts.
#
#   powershell -ExecutionPolicy Bypass -File scripts\export-settings.ps1
#   … -To D:\pucksley-settings.zip      # somewhere other than the desktop
#   … -KeepRunning                       # leave the hub here running (and starting with Windows)
#
# Unless told -KeepRunning, it stops the hub here and stops it starting with Windows, so the pucks
# move over to the new one. The zip has the keys in it readable: delete it once it's imported.
param(
  [string]$To = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'pucksley-settings.zip'),
  [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression.FileSystem

$data = if ($env:PUCKSLEY_DATA) { $env:PUCKSLEY_DATA } else { Join-Path $env:APPDATA 'Pucksley' }
$settings = Join-Path $data 'settings.json'
if (-not (Test-Path $settings)) { throw "There are no Pucksley settings at $settings." }

if (-not $KeepRunning) {
  $hub = Get-Process -Name Pucksley -ErrorAction SilentlyContinue
  if ($hub) {
    Write-Host 'Stopping the hub on this PC'
    $hub | Stop-Process -Force
    $hub | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
  }
  Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Pucksley' -ErrorAction SilentlyContinue
}

# Every secret is a JSON string "enc:d:<base64>" (the JSON writer spells + as \u002B). Each becomes
# "enc:p:<the secret, base64>"; the rest of the file stays exactly as it was.
$script:moved = 0
$script:unreadable = 0
$text = [IO.File]::ReadAllText($settings)
$text = [regex]::Replace($text, '"enc:d:((?:[A-Za-z0-9/=+]|\\u002B|\\/)+)"', {
  param($m)
  $base64 = $m.Groups[1].Value.Replace('\u002B', '+').Replace('\/', '/')
  try {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($base64), $null, 'CurrentUser')
    $script:moved++
    '"enc:p:' + [Convert]::ToBase64String($plain) + '"'
  } catch {
    $script:unreadable++
    $m.Value
  }
})

# Everything but the logs and the downloaded speech models (the new computer fetches its own)
$staging = Join-Path ([IO.Path]::GetTempPath()) ('pucksley-export-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
  Get-ChildItem -Path $data -Force | Where-Object { $_.Name -notin @('logs', 'settings.json') } |
    Copy-Item -Destination $staging -Recurse -Force
  $models = Join-Path $staging 'plugin-data\whisper-local'
  if (Test-Path $models) { Remove-Item $models -Recurse -Force }
  [IO.File]::WriteAllText((Join-Path $staging 'settings.json'), $text, (New-Object Text.UTF8Encoding $false))

  if (Test-Path $To) { Remove-Item $To -Force }
  [IO.Compression.ZipFile]::CreateFromDirectory($staging, $To)
} finally {
  Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Saved $To ($($script:moved) keys and pairings in it)."
if ($script:unreadable -gt 0) { Write-Warning "$($script:unreadable) couldn't be read (saved by another Windows user?): type those in again on the new computer." }
if (-not $KeepRunning) { Write-Host 'The hub on this PC is stopped and no longer starts with Windows.' }
Write-Host 'The zip has your keys in it: delete it once the other computer has them.'
