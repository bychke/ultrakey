"""Regression tests for the keying core.

Generates synthetic scenes with ground-truth alpha, runs the CLI harness and checks the matte error
stays below fixed limits. Usage:  python3 tests/run_tests.py <path to UltraKey.Cli executable or dll>
"""
import os, subprocess, sys, tempfile
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
cli = sys.argv[1]
cmd = ["dotnet", cli] if cli.endswith(".dll") else [cli]

# (sample, settings, max SAD in thousands, max hair mid-coverage error)
CASES = [
    ("green", ["Mode=GreenScreen"], 4.5, 0.08),
    ("green", ["Mode=Auto"], 4.5, 0.08),
    ("blue", ["Mode=BlueScreen"], 3.5, 0.08),
    ("blue", ["Mode=Auto"], 3.5, 0.08),
    ("studio", ["Mode=Auto"], 7.0, 0.15),
    ("white", ["Mode=Auto"], 20.0, 0.15),
]


def read_pam(p):
    with open(p, "rb") as f:
        hdr = {}
        while True:
            line = f.readline().decode().strip()
            if line == "ENDHDR":
                break
            parts = line.split()
            if len(parts) >= 2:
                hdr[parts[0]] = parts[1]
        w, h, d = int(hdr["WIDTH"]), int(hdr["HEIGHT"]), int(hdr["DEPTH"])
        return np.frombuffer(f.read(w * h * d), np.uint8).reshape(h, w, d)


def write_pam(p, arr):
    h, w, d = arr.shape
    with open(p, "wb") as f:
        f.write(f"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH {d}\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n".encode())
        f.write(arr.tobytes())


with tempfile.TemporaryDirectory() as tmp:
    subprocess.run([sys.executable, os.path.join(HERE, "tools", "make_samples.py"), tmp], check=True, stdout=subprocess.DEVNULL)
    failed = 0
    for name, settings, max_sad, max_hair in CASES:
        src = os.path.join(tmp, name + ".pam")
        if not os.path.exists(src):
            write_pam(src, np.asarray(Image.open(os.path.join(tmp, name + ".png")).convert("RGBA")))
        out = os.path.join(tmp, "out.pam")
        subprocess.run(cmd + [src, out] + settings, check=True, stdout=subprocess.DEVNULL)
        a = read_pam(out)[..., 3].astype(float) / 255
        gt = np.asarray(Image.open(os.path.join(tmp, name + "_alpha.png")), float) / 255
        sad = np.abs(a - gt).sum() / 1000
        hair = (np.mgrid[0:gt.shape[0], 0:gt.shape[1]][0] < 170) & (gt > 0.3) & (gt < 0.7)
        hair_err = abs(a[hair].mean() - gt[hair].mean())
        ok = sad <= max_sad and hair_err <= max_hair
        failed += not ok
        print(f"{'PASS' if ok else 'FAIL'}  {name:7s} {' '.join(settings):18s} SAD={sad:6.2f}k (max {max_sad})  hair coverage error={hair_err:.3f} (max {max_hair})")
    sys.exit(1 if failed else 0)
