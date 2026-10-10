# Installs UltraKey into Paint.NET. Run in the folder with UltraKey.zip (or with the repo cloned):
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -ModelsOnly     (only download the AI models)
# If UltraKey.zip is missing, the download page opens in the browser (you are logged in to GitHub there).
# The BiRefNet AI models (MIT license, ~600 MB) are downloaded from Hugging Face into %LOCALAPPDATA%\UltraKey\models.
param([switch]$ModelsOnly)
$ErrorActionPreference = "Stop"

$models = Join-Path $env:LOCALAPPDATA "UltraKey\models"
$downloads = @(
    @{ File = "birefnet.onnx"; Size = "490 MB"
       Url = "https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/534d3c82d3bb8b2f0867db6dfbc3a525b8e42f67/onnx/model_fp16.onnx"
       Sha256 = "3654c741eb80bd926ada8fed1713b506ccf8d30eb1f6487e87eb9f234f33df09" },
    @{ File = "birefnet-lite.onnx"; Size = "115 MB"
       Url = "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/de15b22ba131738a16dff04aab8bdf8dc32e3ac1/onnx/model_fp16.onnx"
       Sha256 = "d39b897ceb16ae654c1731f3dba0cf9b368d9cae74b5a57459b455cc8bfec402" }
)
New-Item -ItemType Directory -Force $models | Out-Null
foreach ($d in $downloads) {
    $path = Join-Path $models $d.File
    if (Test-Path $path) { continue }
    Write-Host "Pobieranie modelu AI $($d.File) ($($d.Size))..."
    $part = "$path.part"
    curl.exe -L --fail --retry 3 -C - -o $part $d.Url
    if ($LASTEXITCODE -ne 0) { throw "Nie udalo sie pobrac $($d.Url)" }
    if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $d.Sha256) {
        Remove-Item $part
        throw "Pobrany plik $($d.File) jest uszkodzony - uruchom skrypt ponownie."
    }
    Move-Item -Force $part $path
}
Write-Host "Modele AI sa w $models"
if ($ModelsOnly) { return }

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
