"""PNG <-> PAM conversion and matte scoring helpers for the CLI harness.

  pam.py topam in.png out.pam
  pam.py topng in.pam out.png
  pam.py score result.pam truth_alpha.png      -> prints SAD / MSE of the alpha channel
  pam.py preview result.pam out.png             -> result composited over a checkerboard, side by side with its matte
"""
import sys
import numpy as np
from PIL import Image


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
        f.write(f"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH {d}\nMAXVAL 255\nTUPLTYPE {'RGB_ALPHA' if d == 4 else 'RGB'}\nENDHDR\n".encode())
        f.write(arr.tobytes())


cmd = sys.argv[1]
if cmd == "topam":
    write_pam(sys.argv[3], np.asarray(Image.open(sys.argv[2]).convert("RGBA")))
elif cmd == "topng":
    Image.fromarray(read_pam(sys.argv[2]), "RGBA").save(sys.argv[3])
elif cmd == "score":
    res = read_pam(sys.argv[2])[..., 3].astype(float) / 255
    gt = np.asarray(Image.open(sys.argv[3]).convert("L"), float) / 255
    sad = np.abs(res - gt).sum() / 1000
    mse = ((res - gt) ** 2).mean() * 1000
    edge = (gt > 0.01) & (gt < 0.99)
    print(f"SAD={sad:.1f}k  MSE={mse:.2f}e-3  edgeMAE={np.abs(res - gt)[edge].mean():.3f}")
elif cmd == "preview":
    r = read_pam(sys.argv[2]).astype(float)
    h, w, _ = r.shape
    yy, xx = np.mgrid[0:h, 0:w]
    checker = np.where(((xx // 16) + (yy // 16)) % 2 == 0, 200, 120)[..., None].repeat(3, -1).astype(float)
    a = r[..., 3:4] / 255
    over = r[..., :3] * a + checker * (1 - a)
    matte = np.repeat(r[..., 3:4], 3, -1)
    Image.fromarray(np.concatenate([over, matte], 1).astype(np.uint8)).save(sys.argv[3])
