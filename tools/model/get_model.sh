#!/usr/bin/env bash
# Downloads U²-Net (Apache-2.0, distributed by the rembg project) and converts it to tools/model/u2net.ukm.
# Requires: python3 with numpy and onnx (pip install numpy onnx).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
[ -f "$here/u2net.onnx" ] || curl -sSfL -o "$here/u2net.onnx" https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx
python3 "$here/convert_u2net.py" "$here/u2net.onnx" "$here/u2net.ukm"
