from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "MiniFences" / "Assets"
ASSET_DIR.mkdir(parents=True, exist_ok=True)


def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def render(size: int) -> Image.Image:
    scale = 4
    canvas_size = size * scale
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def p(value):
        return round(value * canvas_size / 256)

    # Blue desktop tile with a restrained Windows-style vertical highlight.
    gradient = Image.new("RGBA", image.size, (0, 0, 0, 0))
    gradient_draw = ImageDraw.Draw(gradient)
    for y in range(p(8), p(248)):
        progress = (y - p(8)) / max(1, p(240))
        color = (
            round(75 - 34 * progress),
            round(137 - 49 * progress),
            round(255 - 25 * progress),
            255,
        )
        gradient_draw.line((p(8), y, p(248), y), fill=color, width=1)
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((p(8), p(8), p(248), p(248)), radius=p(56), fill=255)
    image.alpha_composite(Image.composite(gradient, Image.new("RGBA", image.size), mask))
    draw = ImageDraw.Draw(image)

    # Main fence window. At tiny sizes the larger blocks remain readable.
    rounded(draw, (p(46), p(54), p(210), p(198)), p(22), "#FAFCFF")
    rounded(draw, (p(46), p(54), p(210), p(96)), p(22), "#DCE8FF")
    draw.rectangle((p(46), p(76), p(210), p(96)), fill="#DCE8FF")
    draw.ellipse((p(63), p(68), p(73), p(78)), fill="#74A3FA")

    rounded(draw, (p(66), p(112), p(120), p(170)), p(12), "#2EC878")
    rounded(draw, (p(130), p(112), p(184), p(138)), p(9), "#9FC0FF")
    rounded(draw, (p(130), p(146), p(184), p(170)), p(9), "#D7E5FF")
    rounded(draw, (p(74), p(183), p(182), p(193)), p(5), "#FFFFFF")

    return image.resize((size, size), Image.Resampling.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [render(size) for size in sizes]
frames[-1].save(ASSET_DIR / "AppIcon.png")
frames[-1].save(
    ASSET_DIR / "AppIcon.ico",
    format="ICO",
    append_images=frames[:-1],
    sizes=[(size, size) for size in sizes],
)

print(ASSET_DIR / "AppIcon.ico")
