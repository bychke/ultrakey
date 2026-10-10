# Installs UltraKey into Paint.NET. Run in the folder with UltraKey.zip (or with the repo cloned):
#   powershell -ExecutionPolicy Bypass -File install.ps1
# If UltraKey.zip is missing, the download page opens in the browser (you are logged in to GitHub there).
$ErrorActionPreference = "Stop"
$zip = Join-Path $PSScriptRoot "UltraKey.zip"
if (-not (Test-Path $zip)) { $zip = Join-Path $PSScriptRoot "dist\UltraKey.zip" }
if (-not (Test-Path $zip)) {
    Start-Process "https://github.com/bychke/paintnet-plugin/raw/claude/wizardly-meitner-ofm1cz/dist/UltraKey.zip"
    $zip = Join-Path ([Environment]::GetFolderPath("UserProfile")) "Downloads\UltraKey.zip"
    Read-Host "Pobieranie otwarte w przegladarce. Gdy UltraKey.zip bedzie w Pobranych, nacisnij Enter"
}
$tmp = Join-Path $env:TEMP "UltraKey"
Expand-Archive -Force $zip $tmp
$dll = Join-Path $tmp "UltraKey.dll"
$target = "C:\Program Files\Paint.NET\Effects"
Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command New-Item -ItemType Directory -Force '$target' | Out-Null; Copy-Item -Force '$dll' '$target\'"
Write-Host "UltraKey zainstalowany w $target - uruchom ponownie Paint.NET."
