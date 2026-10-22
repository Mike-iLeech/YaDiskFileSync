"""Генерирует assets/app.ico — иконку EXE: синий круг с белой стрелкой «в облако».

Тот же рисунок, что IconArt.cs рисует в трее (там цвет круга = состояние синхронизации).
Запуск: python tools/make_icon.py  (нужен Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

BLUE = (40, 120, 220, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(size: int) -> Image.Image:
    ss = 4
    big = size * ss
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    g = ImageDraw.Draw(img)
    pad = big * 0.03
    g.ellipse([pad, pad, big - pad, big - pad], fill=BLUE)
    u = big / 24
    w = round(max(ss * 1.4, big * (0.12 if size <= 20 else 0.09)))
    r = w / 2
    lines = [[(12 * u, 18 * u), (12 * u, 6.5 * u)], [(7 * u, 11 * u), (12 * u, 6 * u), (17 * u, 11 * u)]]
    for pts in lines:
        g.line(pts, fill="white", width=w, joint="curve")
        for x, y in pts:                      # круглые концы и стыки
            g.ellipse([x - r, y - r, x + r, y + r], fill="white")
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    out = Path(__file__).resolve().parent.parent / "assets" / "app.ico"
    out.parent.mkdir(exist_ok=True)
    images = [render(s) for s in SIZES]
    images[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    images[-1].save(out.with_name("app-256.png"))
    print(out)


if __name__ == "__main__":
    main()
