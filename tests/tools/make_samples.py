"""Generates synthetic keying test images with ground-truth alpha.

Usage: python3 make_samples.py <out_dir>
Writes <name>.png (composite), <name>_alpha.png (ground truth) for several scenarios.
"""
import sys, os, math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

rng = np.random.default_rng(7)
W, H = 640, 480
S = 4  # supersampling for anti-aliased hair


def to_lin(c):
    c = c / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055) * 255.0


def subject_alpha():
    img = Image.new("L", (W * S, H * S), 0)
    d = ImageDraw.Draw(img)
    # body + head
    d.ellipse([200 * S, 300 * S, 440 * S, 640 * S], fill=255)
    d.ellipse([250 * S, 110 * S, 390 * S, 300 * S], fill=255)
    # arm making an enclosed hole
    d.line([(215 * S, 360 * S), (150 * S, 420 * S), (220 * S, 470 * S)], fill=255, width=26 * S)
    # hair strands
    for k in range(140):
        x0 = rng.uniform(255, 385)
        y0 = rng.uniform(115, 160)
        ang = rng.uniform(-math.pi, 0)
        length = rng.uniform(40, 120)
        pts = []
        x, y = x0, y0
        for t in range(14):
            pts.append((x * S, y * S))
            ang += rng.uniform(-0.25, 0.25)
            x += math.cos(ang) * length / 14
            y += math.sin(ang) * length / 14
        d.line(pts, fill=255, width=max(1, int(rng.uniform(0.6, 1.6) * S)))
    a = np.asarray(img.resize((W, H), Image.BOX), dtype=np.float64) / 255.0
    # semi-transparent "glass" on the right
    yy, xx = np.mgrid[0:H, 0:W]
    glass = ((xx - 520) ** 2 / 40 ** 2 + (yy - 330) ** 2 / 60 ** 2) <= 1
    a = np.where(glass, np.maximum(a, 0.45), a)
    return a


def subject_colors():
    yy, xx = np.mgrid[0:H, 0:W]
    f = np.zeros((H, W, 3))
    f[:] = (180, 60, 50)                                  # red shirt
    head = ((xx - 320) ** 2 / 70 ** 2 + (yy - 205) ** 2 / 95 ** 2) <= 1.1
    f[head] = (224, 172, 140)                             # skin
    hair = yy < 175
    f[hair] = (90, 60, 35)                                # brown hair
    f[(xx > 470)] = (170, 200, 230)                       # light blue glass
    stripes = ((xx // 12) % 2 == 0) & (yy > 380) & ~(xx > 470)
    f[stripes] = (240, 240, 235)                          # white stripes (enclosed-white test)
    return f


def background(kind):
    yy, xx = np.mgrid[0:H, 0:W]
    if kind == "green":
        base = np.array([30, 170, 70], float)
        vign = 1.0 - 0.45 * (((xx - W / 2) / W) ** 2 + ((yy - H / 2) / H) ** 2) * 2
        crease = 1.0 - 0.18 * np.exp(-((xx - 0.8 * yy - 60) ** 2) / (2 * 25 ** 2))
        b = base[None, None, :] * (vign * crease)[..., None]
    elif kind == "blue":
        base = np.array([20, 70, 190], float)
        grad = 0.75 + 0.35 * (xx / W)
        b = base[None, None, :] * grad[..., None]
    elif kind == "white":
        g = 250 - 40 * (yy / H) ** 2
        b = np.stack([g, g, g * 0.99], -1)
    else:  # studio gradient grey-blue
        t = (yy / H)
        b = np.stack([90 + 70 * t, 100 + 70 * t, 120 + 60 * t], -1)
    return b


def make(name, kind, spill_color):
    a = subject_alpha()
    f = to_lin(subject_colors())
    b = to_lin(background(kind))
    # spill near the subject edge
    if spill_color is not None:
        edge = np.asarray(Image.fromarray((a * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6)), float) / 255.0
        spill = np.clip(1.0 - edge, 0, 1) * 0.35
        f = f + spill[..., None] * (to_lin(np.array(spill_color, float)) - f) * 0.6
    comp = a[..., None] * f + (1 - a[..., None]) * b
    out = to_srgb(comp) + rng.normal(0, 1.5, comp.shape)
    Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8), "RGB").save(os.path.join(OUT, name + ".png"))
    Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8), "L").save(os.path.join(OUT, name + "_alpha.png"))


OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
make("green", "green", (30, 170, 70))
make("blue", "blue", (20, 70, 190))
make("white", "white", None)
make("studio", "studio", None)
print("ok")
