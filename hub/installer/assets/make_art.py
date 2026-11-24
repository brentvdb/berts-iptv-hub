"""Generates the neon artwork for Bert's IPTV Hub (icon, logo, installer wizard images).

    python make_art.py        (needs Pillow)

Outputs next to this script: hub.ico, logo.png, wizard*.bmp, wizard-small*.bmp
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
FONT_DIR = "/usr/share/fonts/truetype/google-fonts"
CYAN = (34, 230, 255)
MAGENTA = (255, 43, 214)
BG = (6, 10, 22)


def font(name, size):
    for path in (os.path.join(FONT_DIR, name), os.path.join(HERE, name)):
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default(size)


def gradient(size, c1=CYAN, c2=MAGENTA, diagonal=True):
    w, h = size
    g = Image.new("RGB", size)
    px = g.load()
    for y in range(h):
        for x in range(w):
            t = ((x / max(1, w - 1)) * 0.75 + (y / max(1, h - 1)) * 0.25) if diagonal else x / max(1, w - 1)
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))
    return g


def neon(mask, colors, glow_radius):
    """Colour a white-on-black mask with a gradient and add a soft glow."""
    layer = colors.copy().convert("RGBA")
    layer.putalpha(mask)
    glow = layer.filter(ImageFilter.GaussianBlur(glow_radius))
    glow2 = layer.filter(ImageFilter.GaussianBlur(glow_radius * 2.5))
    out = Image.new("RGBA", mask.size, (0, 0, 0, 0))
    out = Image.alpha_composite(out, glow2)
    out = Image.alpha_composite(out, glow)
    out = Image.alpha_composite(out, glow)
    out = Image.alpha_composite(out, layer)
    # bright core
    core = Image.new("RGBA", mask.size, (255, 255, 255, 0))
    core.putalpha(mask.filter(ImageFilter.MinFilter(3)).point(lambda v: int(v * 0.35)))
    return Image.alpha_composite(out, core)


def draw_glyph(d, s, ox, oy, scale, stroke):
    """TV with play button, broadcast waves and a small server tower (one source → many screens)."""
    S = lambda v: int(v * scale)
    # waves above the TV
    cx, cy = ox + S(560), oy + S(250)
    for i, r in enumerate((70, 125, 180)):
        d.arc((cx - S(r), cy - S(r), cx + S(r), cy + S(r)), 215, 325, fill=255, width=stroke)
    # TV body
    d.rounded_rectangle((ox + S(330), oy + S(300), ox + S(790), oy + S(590)), radius=S(34), outline=255, width=stroke)
    d.line((ox + S(560), oy + S(590), ox + S(560), oy + S(640)), fill=255, width=stroke)
    d.line((ox + S(470), oy + S(645), ox + S(650), oy + S(645)), fill=255, width=stroke)
    # play triangle
    d.polygon([(ox + S(515), oy + S(375)), (ox + S(515), oy + S(515)), (ox + S(635), oy + S(445))], fill=255)
    # server tower
    d.rounded_rectangle((ox + S(150), oy + S(330), ox + S(275), oy + S(640)), radius=S(18), outline=255, width=stroke)
    for k in range(3):
        y = oy + S(385 + k * 42)
        d.line((ox + S(180), y, ox + S(245), y), fill=255, width=max(2, stroke // 2))
    d.ellipse((ox + S(198), oy + S(575), ox + S(228), oy + S(605)), fill=255)
    # link tower → TV
    d.line((ox + S(275), oy + S(445), ox + S(330), oy + S(445)), fill=255, width=max(2, stroke // 2))


def make_icon(size=1024, with_text=True):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    m = int(size * 0.07)
    r = int(size * 0.19)
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle((m, m, size - m, size - m), radius=r, fill=BG + (255,))
    img = Image.alpha_composite(img, base)

    grad = gradient((size, size))
    # border
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((m + size * 0.03, m + size * 0.03, size - m - size * 0.03, size - m - size * 0.03),
                                           radius=int(r * 0.85), outline=255, width=max(2, int(size * 0.018)))
    img = Image.alpha_composite(img, neon(mask, grad, size * 0.012))

    gmask = Image.new("L", (size, size), 0)
    gd = ImageDraw.Draw(gmask)
    if with_text:
        draw_glyph(gd, size, int(size * 0.105), int(size * 0.09), size / 1024 * 0.86, max(2, int(size * 0.017)))
    else:
        draw_glyph(gd, size, int(size * 0.05), int(size * 0.15), size / 1024 * 0.98, max(2, int(size * 0.032)))
    img = Image.alpha_composite(img, neon(gmask, grad, size * 0.01))

    if with_text:
        tmask = Image.new("L", (size, size), 0)
        td = ImageDraw.Draw(tmask)
        f1 = font("Poppins-BoldItalic.ttf", int(size * 0.135))
        f2 = font("Poppins-Bold.ttf", int(size * 0.085))
        td.text((size / 2, size * 0.715), "BERT'S", font=f1, anchor="mm", fill=255)
        cyan_grad = gradient((size, size), (120, 245, 255), CYAN, diagonal=False)
        img = Image.alpha_composite(img, neon(tmask, cyan_grad, size * 0.008))
        t2 = Image.new("L", (size, size), 0)
        ImageDraw.Draw(t2).text((size / 2, size * 0.835), "IPTV  HUB", font=f2, anchor="mm", fill=255)
        mag_grad = gradient((size, size), MAGENTA, (255, 120, 235), diagonal=False)
        img = Image.alpha_composite(img, neon(t2, mag_grad, size * 0.008))
        bar = Image.new("L", (size, size), 0)
        ImageDraw.Draw(bar).line((size * 0.3, size * 0.895, size * 0.7, size * 0.895), fill=255, width=max(2, int(size * 0.008)))
        img = Image.alpha_composite(img, neon(bar, grad, size * 0.006))
    return img


def flatten(img, bg=BG):
    out = Image.new("RGB", img.size, bg)
    out.paste(img, mask=img.split()[3])
    return out


def wizard_big(w, h):
    canvas = Image.new("RGBA", (w, h), BG + (255,))
    # subtle vertical neon lines
    lines = Image.new("L", (w, h), 0)
    ld = ImageDraw.Draw(lines)
    for i, x in enumerate((0.12, 0.88)):
        ld.line((w * x, h * 0.05, w * x, h * 0.95), fill=110, width=max(1, w // 90))
    canvas = Image.alpha_composite(canvas, neon(lines, gradient((w, h), CYAN, MAGENTA, diagonal=False), w * 0.02))
    logo = make_icon(1024, with_text=True).resize((int(w * 0.86), int(w * 0.86)), Image.LANCZOS)
    canvas.alpha_composite(logo, (int(w * 0.07), int(h * 0.12)))
    t = Image.new("L", (w, h), 0)
    td = ImageDraw.Draw(t)
    td.text((w / 2, h * 0.70), "One account.", font=font("Poppins-Bold.ttf", int(w * 0.095)), anchor="mm", fill=255)
    td.text((w / 2, h * 0.77), "Every TV.", font=font("Poppins-Bold.ttf", int(w * 0.095)), anchor="mm", fill=255)
    canvas = Image.alpha_composite(canvas, neon(t, gradient((w, h), CYAN, MAGENTA, diagonal=False), w * 0.01))
    return flatten(canvas)


def tv_banner(w=640, h=360):
    """Android TV home-screen banner (320x180 dp at 2x)."""
    canvas = Image.new("RGBA", (w, h), BG + (255,))
    frame = Image.new("L", (w, h), 0)
    ImageDraw.Draw(frame).rounded_rectangle((10, 10, w - 10, h - 10), radius=34, outline=255, width=6)
    canvas = Image.alpha_composite(canvas, neon(frame, gradient((w, h)), 5))
    logo = make_icon(1024, with_text=False).resize((int(h * 0.82), int(h * 0.82)), Image.LANCZOS)
    canvas.alpha_composite(logo, (int(w * 0.035), int(h * 0.09)))
    t1 = Image.new("L", (w, h), 0)
    ImageDraw.Draw(t1).text((w * 0.70, h * 0.40), "BERT'S", font=font("Poppins-BoldItalic.ttf", int(h * 0.25)), anchor="mm", fill=255)
    canvas = Image.alpha_composite(canvas, neon(t1, gradient((w, h), (120, 245, 255), CYAN, diagonal=False), 4))
    t2 = Image.new("L", (w, h), 0)
    ImageDraw.Draw(t2).text((w * 0.70, h * 0.66), "IPTV HUB", font=font("Poppins-Bold.ttf", int(h * 0.16)), anchor="mm", fill=255)
    canvas = Image.alpha_composite(canvas, neon(t2, gradient((w, h), MAGENTA, (255, 120, 235), diagonal=False), 4))
    return flatten(canvas)


def main():
    full = make_icon(1024, True)
    simple = make_icon(1024, False)
    full.resize((512, 512), Image.LANCZOS).save(os.path.join(HERE, "logo.png"))

    # Android TV app artwork (tv/app/src/main/res/drawable)
    tv_res = os.path.normpath(os.path.join(HERE, "..", "..", "..", "tv", "app", "src", "main", "res", "drawable"))
    if os.path.isdir(tv_res):
        full.resize((432, 432), Image.LANCZOS).save(os.path.join(tv_res, "ic_launcher.png"))
        tv_banner().save(os.path.join(tv_res, "banner.png"))

    # .ico: detailed art for big sizes, simplified glyph for small ones
    big = full.resize((256, 256), Image.LANCZOS)
    small_sizes = [16, 24, 32, 48, 64]
    frames = [simple.resize((s, s), Image.LANCZOS) for s in small_sizes] + [full.resize((128, 128), Image.LANCZOS)]
    big.save(os.path.join(HERE, "hub.ico"), format="ICO",
             sizes=[(s, s) for s in small_sizes] + [(128, 128), (256, 256)], append_images=frames)

    for suffix, scale in (("", 1.0), ("-150", 1.5), ("-200", 2.0)):
        wizard_big(int(164 * scale), int(314 * scale)).save(os.path.join(HERE, f"wizard{suffix}.bmp"))
        n = int(55 * scale)
        flatten(simple.resize((n, n), Image.LANCZOS), (255, 255, 255)).save(os.path.join(HERE, f"wizard-small{suffix}.bmp"))


if __name__ == "__main__":
    main()
