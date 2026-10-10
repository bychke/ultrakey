# Installs UltraKey into Paint.NET. Run in the folder with UltraKey.zip (or with the repo cloned):
#   powershell -ExecutionPolicy Bypass -File install.ps1
# If UltraKey.zip is missing and the GitHub CLI is logged in, it is downloaded from the repository.
$ErrorActionPreference = "Stop"
$zip = Join-Path $PSScriptRoot "UltraKey.zip"
if (-not (Test-Path $zip)) { $zip = Join-Path $PSScriptRoot "dist\UltraKey.zip" }
if (-not (Test-Path $zip)) {
    $zip = Join-Path $env:TEMP "UltraKey.zip"
    Write-Host "Pobieram UltraKey.zip przez GitHub CLI..."
    gh api -H "Accept: application/vnd.github.raw" "repos/bychke/paintnet-plugin/contents/dist/UltraKey.zip?ref=claude/wizardly-meitner-ofm1cz" > $zip
}
$tmp = Join-Path $env:TEMP "UltraKey"
Expand-Archive -Force $zip $tmp
$dll = Join-Path $tmp "UltraKey.dll"
$target = "C:\Program Files\Paint.NET\Effects"
Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command New-Item -ItemType Directory -Force '$target' | Out-Null; Copy-Item -Force '$dll' '$target\'"
Write-Host "UltraKey zainstalowany w $target - uruchom ponownie Paint.NET."
