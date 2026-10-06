"""
Generates MoogleMap's icon (icon.png, 512x512) from one set of constants.

It keeps the panel language of FateLimit, LootView and HitSpark - deep-navy glass and brass - so
the plugins read as siblings. The subject is the overlay itself: a round lens of dark glass with
a brass compass ring, cave rooms traced in glowing walls the way the map draws them, softening
towards the rim, you in the middle with your view cone, and a moogle's pom-pom bobbing over the
top of the ring.

Run from the repository root:  python tools/make_icon.py
"""
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# ---------------------------------------------------------------- palette ------
INK_TOP = (0x1A, 0x24, 0x3A)
INK_BOTTOM = (0x05, 0x08, 0x0F)
GLASS = (0x0B, 0x13, 0x22)
GOLD = (0xD6, 0xB0, 0x68)
GOLD_BRIGHT = (0xF6, 0xDE, 0xA2)
GOLD_DEEP = (0x8A, 0x6A, 0x2E)
PARCHMENT = (0xF4, 0xEB, 0xD6)

FLOOR = (0x4E, 0x8F, 0xD6)
WALL = (0xB9, 0xE6, 0xFF)
WALL_HOT = (0xF0, 0xFA, 0xFF)
ENEMY = (0xFF, 0x5A, 0x4E)
CHEST = (0xC4, 0x93, 0xFF)
POM = (0xFF, 0x3E, 0x6C)
POM_HOT = (0xFF, 0xC4, 0xD2)

# ---------------------------------------------------------------- geometry -----
SIZE = 512
CORNER = 112
CX, CY = 256, 290          # lens centre, low enough to leave room for the pom-pom
LENS_R = 178
RING_W = 8

# A small dungeon: rooms as rounded rectangles and round halls, joined by corridors. They are
# melted together and their corners softened, like the outlines MoogleMap traces.
RECTS = [(206, 244, 306, 334), (216, 136, 296, 196), (132, 234, 178, 276), (282, 372, 342, 420)]
CIRCLES = [(392, 290, 34), (164, 382, 30)]
CORRIDORS = [((256, 196), (256, 244), 20), ((306, 290), (362, 290), 18), ((178, 255), (206, 255), 16),
             ((220, 334), (180, 366), 18), ((300, 334), (300, 372), 16)]

# The dungeon is turned a little, the way the overlay turns with your camera.
TURN = 22

PLAYER = (256, 292)
ENEMIES = [(398, 282, 9), (380, 306, 7), (160, 380, 8), (238, 164, 8), (276, 156, 7)]
CHESTS = [(318, 400)]


def layer(n):
    return Image.new('RGBA', (n, n), (0, 0, 0, 0))


def fade(img, alpha):
    r, g, b, a = img.split()
    return Image.merge('RGBA', (r, g, b, a.point(lambda v: int(v * alpha))))


def tinted(mask, colour, alpha=1.0):
    img = Image.new('RGBA', mask.size, colour + (0,))
    img.putalpha(mask.point(lambda v: int(v * alpha)))
    return img


def times(img, field):
    """Multiplies a layer's alpha by a 0..1 field."""
    a = np.asarray(img.getchannel('A'), np.float32) * field
    img.putalpha(Image.fromarray(np.clip(a, 0, 255).astype(np.uint8), 'L'))
    return img


def render_png(path, size=SIZE, ss=4):
    n = size * ss
    k = n / SIZE
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    dist = np.sqrt((xs - CX * k) ** 2 + (ys - CY * k) ** 2) / k

    # --- panel ----------------------------------------------------------------
    ramp = np.linspace(0.0, 1.0, n, dtype=np.float32)[:, None]
    base = np.zeros((n, n, 3), dtype=np.float32)
    for c in range(3):
        base[:, :, c] = INK_TOP[c] * (1 - ramp) + INK_BOTTOM[c] * ramp
    halo = np.clip(1 - np.abs(dist - LENS_R) / 60, 0, 1) ** 2
    for c in range(3):
        base[:, :, c] += GOLD[c] * halo * 0.10
    panel = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    panel_mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(panel_mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=CORNER * k, fill=255)
    panel.putalpha(panel_mask)
    out = panel

    # --- the lens: dark glass, lighter toward the middle --------------------------
    inside = np.clip((LENS_R - dist) * k, 0, 1)
    glass = np.zeros((n, n, 3), dtype=np.float32)
    lift = np.clip(1 - dist / LENS_R, 0, 1) ** 1.5
    for c in range(3):
        glass[:, :, c] = GLASS[c] + FLOOR[c] * 0.10 * lift
    lens = Image.fromarray(np.clip(glass, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    lens.putalpha(Image.fromarray((inside * 255).astype(np.uint8), 'L'))
    out = Image.alpha_composite(out, lens)

    # --- the caves: metaballs thresholded into one smooth floor ----------------------
    blobs = Image.new('L', (n, n), 0)
    bd = ImageDraw.Draw(blobs)
    for x0, y0, x1, y1 in RECTS:
        bd.rounded_rectangle([x0 * k, y0 * k, x1 * k, y1 * k], radius=8 * k, fill=255)
    for x, y, r in CIRCLES:
        bd.ellipse([(x - r) * k, (y - r) * k, (x + r) * k, (y + r) * k], fill=255)
    for (x0, y0), (x1, y1), w in CORRIDORS:
        bd.line([(x0 * k, y0 * k), (x1 * k, y1 * k)], fill=255, width=int(w * k))
    blobs = blobs.rotate(TURN, resample=Image.BICUBIC, center=(CX * k, CY * k))
    soft = np.asarray(blobs.filter(ImageFilter.GaussianBlur(3.5 * k)), np.float32) / 255
    floor = Image.fromarray(((soft > 0.5) * 255).astype(np.uint8), 'L').filter(ImageFilter.GaussianBlur(0.8 * k))
    inner = floor.filter(ImageFilter.MinFilter(int(7 * k) | 1))
    edge = Image.fromarray(np.clip(np.asarray(floor, np.int16) - np.asarray(inner, np.int16), 0, 255).astype(np.uint8), 'L')

    # The rim fade: everything inside the lens softens out over its outer third.
    t = np.clip((dist / LENS_R - 0.62) / 0.34, 0, 1)
    rim = (1 - t * t * (3 - 2 * t)) * inside

    out = Image.alpha_composite(out, times(tinted(floor, FLOOR, 0.42), rim))
    out = Image.alpha_composite(out, times(tinted(edge.filter(ImageFilter.GaussianBlur(9 * k)), WALL, 0.85), rim))
    out = Image.alpha_composite(out, times(tinted(edge, WALL, 1.0), rim))
    out = Image.alpha_composite(out, times(tinted(edge.filter(ImageFilter.MinFilter(3)), WALL_HOT, 0.8), rim))

    # --- view cone, enemies, a coffer ---------------------------------------------
    px, py = PLAYER
    cone = layer(n)
    cd = ImageDraw.Draw(cone)
    for reach, a in [(110, 0.10), (80, 0.12), (52, 0.16)]:
        cd.pieslice([(px - reach) * k, (py - reach) * k, (px + reach) * k, (py + reach) * k],
                    start=-90 - 40, end=-90 + 40, fill=PARCHMENT + (int(255 * a),))
    out = Image.alpha_composite(out, times(cone.filter(ImageFilter.GaussianBlur(3 * k)), rim))

    marks = layer(n)
    md = ImageDraw.Draw(marks)
    for x, y, r in ENEMIES:
        md.ellipse([(x - r - 3) * k, (y - r - 3) * k, (x + r + 3) * k, (y + r + 3) * k], fill=(4, 6, 12, 170))
        md.ellipse([(x - r) * k, (y - r) * k, (x + r) * k, (y + r) * k], fill=ENEMY + (255,))
    for x, y in CHESTS:
        md.rounded_rectangle([(x - 9) * k, (y - 7) * k, (x + 9) * k, (y + 7) * k], radius=2 * k, fill=CHEST + (255,))
        md.rectangle([(x - 9) * k, (y - 7) * k, (x + 9) * k, (y - 1) * k], fill=(0xE2, 0xCC, 0xFF, 255))
    marks = marks.rotate(TURN, resample=Image.BICUBIC, center=(CX * k, CY * k))
    out = Image.alpha_composite(out, times(marks, rim))

    # --- you ---------------------------------------------------------------------
    glow = layer(n)
    ImageDraw.Draw(glow).ellipse([(px - 40) * k, (py - 40) * k, (px + 40) * k, (py + 40) * k], fill=GOLD + (255,))
    out = Image.alpha_composite(out, fade(glow.filter(ImageFilter.GaussianBlur(14 * k)), 0.5))
    s = 22
    tip, left, right, notch = (px, py - s * 1.3), (px - s * 0.9, py + s * 0.85), (px + s * 0.9, py + s * 0.85), (px, py + s * 0.35)
    arrow = layer(n)
    ad = ImageDraw.Draw(arrow)
    ad.polygon([(p[0] * k, p[1] * k) for p in (tip, left, notch)], fill=PARCHMENT + (255,))
    ad.polygon([(p[0] * k, p[1] * k) for p in (tip, notch, right)], fill=GOLD_BRIGHT + (255,))
    ad.line([(p[0] * k, p[1] * k) for p in (tip, left, notch, right, tip)], fill=GOLD_DEEP + (255,), width=int(3.5 * k), joint='curve')
    out = Image.alpha_composite(out, arrow)

    # --- brass ring with compass ticks ---------------------------------------------
    ring = layer(n)
    rd = ImageDraw.Draw(ring)
    rd.ellipse([(CX - LENS_R) * k, (CY - LENS_R) * k, (CX + LENS_R) * k, (CY + LENS_R) * k],
               outline=GOLD + (255,), width=int(RING_W * k))
    rd.ellipse([(CX - LENS_R + 14) * k, (CY - LENS_R + 14) * k, (CX + LENS_R - 14) * k, (CY + LENS_R - 14) * k],
               outline=GOLD_DEEP + (200,), width=int(2 * k))
    for deg in range(0, 360, 15):
        a = math.radians(deg)
        major = deg % 90 == 0
        r0, r1 = LENS_R - (24 if major else 18), LENS_R - 14
        rd.line([((CX + math.sin(a) * r0) * k, (CY - math.cos(a) * r0) * k),
                 ((CX + math.sin(a) * r1) * k, (CY - math.cos(a) * r1) * k)],
                fill=(GOLD_BRIGHT if major else GOLD_DEEP) + (255,), width=int((3.5 if major else 2) * k))
    out = Image.alpha_composite(out, fade(ring.filter(ImageFilter.GaussianBlur(5 * k)), 0.45))
    out = Image.alpha_composite(out, ring)

    # A bright highlight across the top of the ring sells it as metal.
    shine = layer(n)
    ImageDraw.Draw(shine).arc([(CX - LENS_R) * k, (CY - LENS_R) * k, (CX + LENS_R) * k, (CY + LENS_R) * k],
                              start=200, end=250, fill=GOLD_BRIGHT + (255,), width=int(4 * k))
    out = Image.alpha_composite(out, fade(shine.filter(ImageFilter.GaussianBlur(1.5 * k)), 0.9))

    # --- the pom-pom, on its stalk over the top of the ring ----------------------------
    sx, sy = CX, CY - LENS_R - 2
    stalk = layer(n)
    pts = [(sx + 9 * math.sin(i / 9 * math.pi * 0.9), sy - i * 3.0) for i in range(10)]
    ImageDraw.Draw(stalk).line([(x * k, y * k) for x, y in pts], fill=(0x7A, 0x52, 0x3E, 255), width=int(8 * k), joint='curve')
    out = Image.alpha_composite(out, stalk)

    bx, by = pts[-1]
    by -= 18
    pr = 24
    halo = layer(n)
    ImageDraw.Draw(halo).ellipse([(bx - pr * 2.2) * k, (by - pr * 2.2) * k, (bx + pr * 2.2) * k, (by + pr * 2.2) * k], fill=POM + (255,))
    out = Image.alpha_composite(out, fade(halo.filter(ImageFilter.GaussianBlur(20 * k)), 0.55))

    pom_mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(pom_mask).ellipse([(bx - pr) * k, (by - pr) * k, (bx + pr) * k, (by + pr) * k], fill=255)
    shade = np.clip(np.sqrt((xs - (bx - pr * 0.35) * k) ** 2 + (ys - (by - pr * 0.4) * k) ** 2) / (pr * 1.7 * k), 0, 1)
    field = np.zeros((n, n, 3), dtype=np.float32)
    for c in range(3):
        field[:, :, c] = POM_HOT[c] * (1 - shade) ** 1.2 + POM[c] * 0.7 * shade
    pom = Image.fromarray(np.clip(field, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    pom.putalpha(pom_mask)
    out = Image.alpha_composite(out, pom)

    spec = layer(n)
    ImageDraw.Draw(spec).ellipse([(bx - pr * 0.55) * k, (by - pr * 0.65) * k, (bx - pr * 0.1) * k, (by - pr * 0.25) * k],
                                 fill=(255, 255, 255, 200))
    out = Image.alpha_composite(out, spec.filter(ImageFilter.GaussianBlur(2 * k)))

    out.putalpha(Image.fromarray(np.minimum(np.asarray(out.getchannel('A')), np.asarray(panel_mask)), 'L'))
    icon = out.resize((size, size), Image.LANCZOS)
    icon.save(path)
    return icon


if __name__ == '__main__':
    render_png('icon.png')
    print('icon.png written')
