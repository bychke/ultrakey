# Builds UltraKey.dll and (optionally) installs it into Paint.NET's user Effects folder.
#   .\build.ps1                 -> build only
#   .\build.ps1 -Install        -> build and copy to Documents\paint.net App Files\Effects
param(
    [string]$PdnDir = "C:\Program Files\paint.net",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
dotnet build "$PSScriptRoot\src\UltraKey" -c Release -p:PdnDir="$PdnDir"
$dll = "$PSScriptRoot\src\UltraKey\bin\Release\net9.0-windows\UltraKey.dll"
Write-Host "Built: $dll"
if ($Install) {
    $effects = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "paint.net App Files\Effects"
    New-Item -ItemType Directory -Force -Path $effects | Out-Null
    Copy-Item $dll $effects -Force
    Write-Host "Installed to $effects - restart Paint.NET."
}
