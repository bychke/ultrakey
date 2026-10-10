# Downloads U²-Net (Apache-2.0, distributed by the rembg project) and converts it to tools\model\u2net.ukm.
# Requires: python with numpy and onnx (pip install numpy onnx).
$ErrorActionPreference = "Stop"
$onnx = Join-Path $PSScriptRoot "u2net.onnx"
if (-not (Test-Path $onnx)) {
    Invoke-WebRequest "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx" -OutFile $onnx
}
python (Join-Path $PSScriptRoot "convert_u2net.py") $onnx (Join-Path $PSScriptRoot "u2net.ukm")
