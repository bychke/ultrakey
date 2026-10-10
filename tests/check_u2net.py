"""Checks the C# U²-Net engine against onnxruntime on a random natural-ish input.
Usage: python3 tests/check_u2net.py <UltraKey.Cli.dll> <u2net.onnx> <u2net.ukm>"""
import os, subprocess, sys, tempfile
import numpy as np
import onnxruntime as ort

cli, onnx_path, ukm = sys.argv[1:4]
rng = np.random.default_rng(3)
x = rng.normal(0, 1, (1, 3, 320, 320)).astype(np.float32)
x[:, :, 80:240, 100:220] += 1.5  # a "subject"
with tempfile.TemporaryDirectory() as tmp:
    x.tofile(os.path.join(tmp, "in.f32"))
    subprocess.run(["dotnet", cli, "u2net", ukm, os.path.join(tmp, "in.f32"), os.path.join(tmp, "out.f32")], check=True)
    out = np.fromfile(os.path.join(tmp, "out.f32"), np.float32)
s = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
ref = s.run(None, {s.get_inputs()[0].name: x})[0].ravel()
diff = np.abs(out - ref).max()
print(f"U2Net engine vs onnxruntime: max abs diff {diff:.5f}")
sys.exit(0 if diff < 0.03 else 1)
