"""Harder, photo-like test scenes: a grey/white furry animal with a global green cast, fur fibres,
JPEG compression (4:2:0 chroma subsampling, like memes) — on a green screen, on black and on white.

Usage: python3 make_fur.py <out_dir>   -> fur_green.png, fur_black.png, fur_white.png + *_alpha.png
"""
import math, os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

rng = np.random.default_rng(11)
W, H, S = 600, 600, 4


def lin(c):
    c = np.asarray(c, float) / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055) * 255.0


def noise(scale, octaves=4):
    out = np.zeros((H, W))
    amp = 1.0
    for o in range(octaves):
        s = max(2, int(scale / (2 ** o)))
        small = rng.random((H // s + 2, W // s + 2))
        out += amp * np.asarray(Image.fromarray((small * 255).astype(np.uint8)).resize((W + 2 * s, H + 2 * s), Image.BICUBIC), float)[s:s + H, s:s + W] / 255
        amp *= 0.5
    return out / (2 - 2 ** (1 - octaves))


def body_alpha():
    img = Image.new("L", (W * S, H * S), 0)
    d = ImageDraw.Draw(img)
    d.ellipse([170 * S, 300 * S, 430 * S, 640 * S], fill=255)            # body
    d.ellipse([200 * S, 130 * S, 400 * S, 330 * S], fill=255)            # head
    d.polygon([(215 * S, 190 * S), (235 * S, 70 * S), (285 * S, 150 * S)], fill=255)   # ears
    d.polygon([(385 * S, 190 * S), (365 * S, 70 * S), (315 * S, 150 * S)], fill=255)
    # fur fibres all around the silhouette
    base = np.asarray(img.resize((W, H), Image.BOX)) > 128
    edge = base & ~np.asarray(Image.fromarray((base * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(3))).astype(bool)
    ys, xs = np.nonzero(edge)
    cy, cx = 330, 300
    for k in rng.choice(len(xs), 1400, replace=False):
        x, y = xs[k], ys[k]
        ang = math.atan2(y - cy, x - cx) + rng.normal(0, 0.5)
        ln = rng.uniform(4, 16)
        pts = [(x * S, y * S)]
        for t in range(5):
            ang += rng.normal(0, 0.15)
            x += math.cos(ang) * ln / 5
            y += math.sin(ang) * ln / 5
            pts.append((x * S, y * S))
        d.line(pts, fill=255, width=max(1, int(rng.uniform(0.4, 1.0) * S)))
    # whiskers
    for side in (-1, 1):
        for k in range(4):
            x0, y0 = 300 + side * 30, 255 + k * 6
            d.line([(x0 * S, y0 * S), ((x0 + side * 110) * S, (y0 - 20 + k * 14) * S)], fill=255, width=S // 2)
    return np.asarray(img.resize((W, H), Image.BOX), float) / 255


def fur_colors():
    yy, xx = np.mgrid[0:H, 0:W]
    n = noise(40)
    stripes = (np.sin((xx * 0.08 + n * 6 + yy * 0.02)) > 0.3).astype(float)
    grey = 150 + 70 * n - 70 * stripes * (yy < 520)
    belly = np.exp(-(((xx - 300) / 70) ** 2 + ((yy - 420) / 120) ** 2))
    grey = grey * (1 - belly) + 235 * belly
    fine = rng.normal(0, 10, (H, W))
    g = np.clip(grey + fine, 20, 250)
    f = np.stack([g * 1.0, g * 0.97, g * 0.93], -1)
    # eyes / nose / mouth
    for ex in (265, 335):
        f[((xx - ex) ** 2 + (yy - 225) ** 2) < 14 ** 2] = (30, 30, 25)
    f[((xx - 300) ** 2 / 9 ** 2 + (yy - 260) ** 2 / 6 ** 2) < 1] = (200, 120, 130)
    f[((xx - 300) ** 2 / 25 ** 2 + (yy - 290) ** 2 / 14 ** 2) < 1] = (120, 40, 60)   # open mouth
    return f


def write(name, comp_lin, alpha):
    out = srgb(comp_lin) + rng.normal(0, 2.0, comp_lin.shape)
    im = Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8), "RGB")
    # memes: JPEG with 4:2:0 chroma subsampling
    path = os.path.join(OUT, name + ".jpg")
    im.save(path, quality=72, subsampling=2)
    Image.open(path).convert("RGB").save(os.path.join(OUT, name + ".png"))
    os.remove(path)
    Image.fromarray(np.clip(alpha * 255 + 0.5, 0, 255).astype(np.uint8), "L").save(os.path.join(OUT, name + "_alpha.png"))


OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
a = body_alpha()
f = lin(fur_colors())
yy, xx = np.mgrid[0:H, 0:W]

# Green screen: uneven, plus the whole animal is lit by green bounce light (strong near the edges).
green = lin(np.array([40, 190, 60]))
screen = green[None, None, :] * (0.75 + 0.35 * noise(200, 2))[..., None]
edge = np.asarray(Image.fromarray((a * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(10)), float) / 255
cast = 0.10 + 0.30 * np.clip(1 - edge, 0, 1)
fg = f * (1 - cast[..., None]) + cast[..., None] * green * f.mean(-1, keepdims=True) / green.mean() * 0.9
write("fur_green", a[..., None] * fg + (1 - a[..., None]) * screen, a)

black = np.full_like(f, lin(8))
write("fur_black", a[..., None] * f + (1 - a[..., None]) * black, a)

white = lin(np.array([245, 245, 242]))[None, None, :] * (0.92 + 0.08 * (yy / H))[..., None]
write("fur_white", a[..., None] * f + (1 - a[..., None]) * white, a)
print("ok")
