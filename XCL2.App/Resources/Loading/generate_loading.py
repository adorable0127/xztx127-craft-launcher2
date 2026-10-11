#!/usr/bin/env python3
"""Rebuild the loading asset from the same geometry as LoadingIndicator.cs.

Run: python generate_loading.py
Build-time dependency: Pillow; the launcher has no Python/Pillow dependency.
GIF has 60 frames × 20 ms = 1.2 seconds, matching the 75% animation-speed setting.
"""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
SIZE = 128
SUPERSAMPLE = 4
SCALE = SIZE * SUPERSAMPLE / 64
FRAMES = 60


def mix(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def polar(radius, degrees):
    angle = math.radians(degrees)
    return ((32 + radius * math.cos(angle)) * SCALE,
            (32 + radius * math.sin(angle)) * SCALE)


def circle(draw, center, radius, color):
    x, y = center
    r = radius * SCALE
    draw.ellipse((x-r, y-r, x+r, y+r), fill=color)


def arc(draw, radius, start, end, color, width):
    # A polyline with round ends mirrors WPF's StreamGeometry + PenLineCap.Round.
    count = max(2, math.ceil((end - start) * radius / 35))
    points = [polar(radius, start + (end - start) * i / count) for i in range(count + 1)]
    draw.line(points, fill=color, width=max(1, round(width * SCALE)), joint="curve")
    circle(draw, points[0], width / 2, color)
    circle(draw, points[-1], width / 2, color)


def render_frame(degrees=0):
    image = Image.new("RGBA", (SIZE * SUPERSAMPLE,) * 2)
    draw = ImageDraw.Draw(image)
    # GIF only supports binary transparency. Use a quiet solid track in the asset;
    # the native control uses translucent equivalents that adapt to both themes.
    arc(draw, 23, 0, 360, (171, 190, 224, 255), 1.6)
    arc(draw, 15, 0, 360, (190, 197, 229, 255), 1)
    core_mask = Image.new("L", image.size)
    ImageDraw.Draw(core_mask).polygon(
        [(32*SCALE, 26.5*SCALE), (37.5*SCALE, 32*SCALE),
         (32*SCALE, 37.5*SCALE), (26.5*SCALE, 32*SCALE)], fill=255)
    core = Image.new("RGBA", image.size)
    core_draw = ImageDraw.Draw(core)
    for y in range(round(26.5*SCALE), round(37.5*SCALE)+1):
        color = mix((104, 168, 255), (157, 114, 248), (y/SCALE - 26.5)/11)
        core_draw.line((26*SCALE, y, 38*SCALE, y), fill=(*color, 255))
    image.paste(core, (0, 0), core_mask)
    draw = ImageDraw.Draw(image)
    for i in range(48):
        t = (i + .5)/48
        color = (mix((64, 189, 250), (90, 126, 255), t/.55) if t < .55
                 else mix((90, 126, 255), (159, 103, 247), (t-.55)/.45))
        # A pale-to-saturated tail reads consistently in a transparent GIF.
        color = mix((174, 208, 245), color, .3 + .7*t)
        arc(draw, 23, degrees - 160 + 248*i/48,
            degrees - 160 + 248*(i+1)/48 + .25, (*color, 255), 3.8)
    circle(draw, polar(23, degrees+88), 2.65, (165, 117, 255, 255))
    arc(draw, 15, degrees+168, degrees+228, (117, 184, 247, 255), 2)
    circle(draw, polar(15, degrees+228), 1.65, (108, 195, 255, 255))
    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def transparent_gif_frame(image):
    # Reserve palette slot 0 for transparency. Quantization uses the unassociated
    # RGB channels, so opaque edges do not acquire a white or black matte.
    rgb = Image.new("RGB", image.size)
    rgb.paste(image.convert("RGB"))
    quantized = rgb.quantize(colors=255, method=Image.Quantize.MEDIANCUT,
                            dither=Image.Dither.NONE)
    palette = quantized.getpalette()[:255*3]
    palette = [0, 0, 0] + palette
    palette += [0] * (768-len(palette))
    alpha = image.getchannel("A").tobytes()
    indices = bytes(0 if a < 96 else index+1
                    for index, a in zip(quantized.tobytes(), alpha))
    result = Image.frombytes("P", image.size, indices)
    result.putpalette(palette)
    result.info["transparency"] = 0
    return result


def main():
    frames = [render_frame(360*i/FRAMES) for i in range(FRAMES)]
    gif_frames = [transparent_gif_frame(frame) for frame in frames]
    gif_frames[0].save(ROOT / "orbit-loading.gif", save_all=True,
                       append_images=gif_frames[1:], duration=20, loop=0,
                       transparency=0, disposal=2, optimize=False)
    frames[0].save(ROOT / "orbit-loading.png")
    # Show actual decoded GIF frames on both themes to expose transparency bugs.
    gif = Image.open(ROOT / "orbit-loading.gif")
    sheet = Image.new("RGB", (768, 432), "#eef2f8")
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default(size=15)
    for i in range(12):
        x, y = (i % 6)*128, (i//6)*216
        dark = i >= 6
        background = "#171c2d" if dark else "#ffffff"
        draw.rounded_rectangle((x+4, y+8, x+124, y+208), radius=15, fill=background)
        gif.seek((i % 6)*10)
        frame = gif.convert("RGBA").resize((104, 104), Image.Resampling.LANCZOS)
        sheet.paste(frame, (x+12, y+45), frame)
        draw.text((x+29, y+170), f"{(i%6)*200:04d} ms", font=font,
                  fill="#aab7d1" if dark else "#61718c")
    sheet.save(ROOT / "orbit-loading-contact-sheet.png")
    print(f"Created {FRAMES} frames, {SIZE}×{SIZE}, 1200 ms, transparent GIF")
    print(f"GIF: {(ROOT / 'orbit-loading.gif').stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
