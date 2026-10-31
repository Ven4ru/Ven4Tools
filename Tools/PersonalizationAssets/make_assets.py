"""Файлы оформления «Венчик» для раздела «Персонализация».

    python Tools/PersonalizationAssets/make_assets.py

Собирает в Ven4Tools/Personalization/Venchik/ курсоры (.cur, .ani), обои и экран
блокировки (.jpg) и звуки (.wav). Исходные рисунки лежат рядом, в art/:
wallpaper.png и lockscreen.png — картинки целиком, paws.png — лист с тремя лапами
на белом фоне (стрелка, лапа для ссылок, стрелка с вопросом). Бег Венчика для
курсора «Подождите» — та же лента, что в заставке. Звуки синтезируются здесь же.
Нужны Pillow, numpy и scipy.
"""
import math
import struct
import wave
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ART = HERE / "art"
OUT = ROOT / "Ven4Tools" / "Personalization" / "Venchik"
RUN = ROOT / "Ven4Tools.Screensaver" / "Resources" / "run.png"

ACCENT = (74, 222, 128)
OUTLINE = (30, 52, 48)
CURSOR_SIZES = (32, 48, 64, 96, 128)
SCREEN = (3840, 2160)


# ---------- курсоры ----------

def cur_bytes(images):
    """Файл .cur: список (картинка RGBA, точка нажатия x, y) разных размеров, 32 бита с альфой."""
    head = struct.pack("<HHH", 0, 2, len(images))
    entries, blobs = b"", b""
    offset = 6 + 16 * len(images)
    for img, hx, hy in images:
        w, h = img.size
        px = np.array(img.convert("RGBA"))[::-1]           # строки снизу вверх
        xor = px[:, :, [2, 1, 0, 3]].tobytes()             # BGRA
        row = ((w + 31) // 32) * 4
        # Маска AND: 1 — прозрачная точка. Windows берёт альфу, маска — для старых путей вывода.
        bits = np.packbits(px[:, :, 3] == 0, axis=1)
        mask = b"".join(r.tobytes().ljust(row, b"\x00") for r in bits)
        info = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
        blob = info + xor + mask
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, hx, hy, len(blob), offset)
        blobs += blob
        offset += len(blob)
    return head + entries + blobs


def sprites():
    """Три лапы с листа paws.png: белый фон, связанный с краем, становится прозрачным."""
    sheet = Image.open(ART / "paws.png").convert("RGB")
    rgb = np.array(sheet).astype(int)
    light = rgb.min(axis=2) > 232
    lab, _ = ndimage.label(light)
    border = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}
    bg = np.isin(lab, list(border))
    alpha = Image.fromarray(np.where(bg, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8))
    rgba = sheet.convert("RGBA")
    rgba.putalpha(alpha)

    parts, n = ndimage.label(ndimage.binary_closing(~bg, iterations=3))
    boxes = []
    for i, sl in enumerate(ndimage.find_objects(parts), 1):
        if (parts[sl] == i).sum() > 2000:
            boxes.append((sl[1].start, sl[0].start, sl[1].stop, sl[0].stop))
    # Порядок на листе: две сверху (стрелка, лапа), одна снизу (стрелка с вопросом).
    boxes.sort(key=lambda b: (b[1] // 300, b[0]))
    return [rgba.crop(b) for b in boxes]


def square(img, anchor):
    """Вписать рисунок в квадрат: стрелки — в левый верхний угол (там кончик), лапу — по центру."""
    side = max(img.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    pos = (0, 0) if anchor == "tip" else ((side - img.width) // 2, 0)
    sq.paste(img, pos, img)
    return sq


def tip(img):
    """Кончик стрелки — непрозрачная точка с наименьшей суммой x + y."""
    a = np.array(img)[:, :, 3] > 128
    ys, xs = np.nonzero(a)
    i = np.argmin(xs + ys)
    return xs[i] / (img.width - 1), ys[i] / (img.height - 1)


def top_center(img):
    a = np.array(img)[:, :, 3] > 128
    ys, xs = np.nonzero(a)
    y0 = ys.min()
    return xs[ys <= y0 + 3].mean() / (img.width - 1), (y0 + img.height * 0.04) / (img.height - 1)


def sizes(img, hot):
    return [(img.resize((s, s), Image.LANCZOS), round(hot[0] * (s - 1)), round(hot[1] * (s - 1))) for s in CURSOR_SIZES]


def busy_frames(arrow, n=8):
    """«Работает в фоне»: стрелка-лапа и бегущие по кругу зелёные подушечки."""
    frames = []
    for i in range(n):
        big = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
        a = arrow.resize((380, 380), Image.LANCZOS)
        big.paste(a, (0, 0), a)
        d = ImageDraw.Draw(big)
        cx, cy, r = 400, 400, 78
        for k in range(3):
            ang = math.radians(i * 360 / n + k * 42)
            x, y = cx + math.cos(ang) * r, cy + math.sin(ang) * r
            rr = 34 - k * 8
            d.ellipse((x - rr, y - rr, x + rr, y + rr), fill=ACCENT + (255 - k * 70,), outline=OUTLINE + (255,), width=8)
        frames.append(sizes(big, tip(big)))
    return frames


def wait_frames():
    """«Подождите»: Венчик бежит на месте — кадры той же ленты, что в заставке."""
    sheet = Image.open(RUN).convert("RGBA")
    n = 14
    fw = sheet.width // n
    frames = []
    for i in range(n):
        frame = sheet.crop((i * fw, 0, (i + 1) * fw, sheet.height))
        side = max(frame.size)
        sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        sq.paste(frame, ((side - frame.width) // 2, side - frame.height), frame)
        frames.append(sizes(sq, (0.5, 0.5)))
    return frames


def chunk(tag, data):
    pad = b"\x00" if len(data) % 2 else b""
    return tag + struct.pack("<I", len(data)) + data + pad


def ani_bytes(frames, jiffies):
    """Анимированный курсор .ani (RIFF ACON); jiffies — длительность кадра в 1/60 с.
    Кадры 128 px Windows в .ani не принимает (LoadCursorFromFile возвращает пустой
    указатель) — в анимации размеры только до 96."""
    icons = b"".join(chunk(b"icon", cur_bytes([x for x in f if x[0].width <= 96])) for f in frames)
    anih = struct.pack("<9I", 36, len(frames), len(frames), 0, 0, 0, 0, jiffies, 1)
    body = b"ACON" + chunk(b"anih", anih) + chunk(b"LIST", b"fram" + icons)
    return b"RIFF" + struct.pack("<I", len(body)) + body


# ---------- обои и экран блокировки ----------

def fullscreen(name):
    """Рисунок растягивается до 4K с сохранением пропорций и обрезается по краям."""
    img = Image.open(ART / name).convert("RGB")
    k = max(SCREEN[0] / img.width, SCREEN[1] / img.height)
    img = img.resize((math.ceil(img.width * k), math.ceil(img.height * k)), Image.LANCZOS)
    left, top = (img.width - SCREEN[0]) // 2, (img.height - SCREEN[1]) // 2
    img = img.crop((left, top, left + SCREEN[0], top + SCREEN[1]))
    return img.filter(ImageFilter.UnsharpMask(radius=2, percent=60, threshold=2))


# ---------- звуки ----------

RATE = 44100


def tone(freq, dur, vol=0.5, attack=0.005, decay=6.0, harmonics=((1, 1.0), (2, 0.25), (3, 0.08))):
    t = np.arange(int(RATE * dur)) / RATE
    f = freq(t) if callable(freq) else np.full_like(t, freq)
    phase = 2 * np.pi * np.cumsum(f) / RATE
    env = np.minimum(1.0, t / attack) * np.exp(-decay * t)
    return vol * env * sum(a * np.sin(phase * k) for k, a in harmonics)


def meow(dur=0.55, vol=0.45):
    """Короткое «мяу»: высота поднимается и опадает, гласная «и-а-у» — сдвигом формант."""
    t = np.arange(int(RATE * dur)) / RATE
    x = t / dur
    f0 = 520 + 380 * np.sin(np.pi * np.minimum(1.0, x * 1.3)) - 120 * x
    phase = 2 * np.pi * np.cumsum(f0) / RATE
    f1 = 400 + 500 * np.sin(np.pi * x)
    f2 = 2200 - 1300 * x
    s = np.zeros_like(t)
    for k in range(1, 14):
        fk = f0 * k
        amp = np.exp(-((fk - f1) / 220) ** 2) + 0.6 * np.exp(-((fk - f2) / 300) ** 2) + 0.05
        s += amp * np.sin(phase * k) / k ** 0.5
    env = np.minimum(1.0, t / 0.04) * np.minimum(1.0, (dur - t) / 0.12)
    return vol * env * s / 3


def mix(*parts):
    end = max(int(start * RATE) + len(s) for start, s in parts)
    total = np.zeros(end)
    for start, s in parts:
        i0 = int(start * RATE)
        total[i0:i0 + len(s)] += s
    return total


def save_wav(path, samples):
    k = 0.85 / max(1e-9, np.abs(samples).max())
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(samples * k, -1, 1) * 32767).astype("<i2").tobytes())


def main():
    OUT.mkdir(parents=True, exist_ok=True)

    arrow, paw, help_arrow = sprites()
    arrow, help_arrow, paw = square(arrow, "tip"), square(help_arrow, "tip"), square(paw, "center")
    (OUT / "paw-arrow.cur").write_bytes(cur_bytes(sizes(arrow, tip(arrow))))
    (OUT / "paw-help.cur").write_bytes(cur_bytes(sizes(help_arrow, tip(help_arrow))))
    (OUT / "paw-hand.cur").write_bytes(cur_bytes(sizes(paw, top_center(paw))))
    (OUT / "paw-busy.ani").write_bytes(ani_bytes(busy_frames(arrow), 6))
    (OUT / "venchik-wait.ani").write_bytes(ani_bytes(wait_frames(), 4))

    fullscreen("wallpaper.png").save(OUT / "wallpaper.jpg", quality=92, optimize=True)
    fullscreen("lockscreen.png").save(OUT / "lockscreen.jpg", quality=92, optimize=True)

    save_wav(OUT / "info.wav", mix((0, tone(880, 0.6)), (0.12, tone(1320, 0.7))))
    save_wav(OUT / "warning.wav", mix((0, tone(988, 0.5)), (0.14, tone(740, 0.7))))
    save_wav(OUT / "error.wav", mix((0, meow(0.5, 0.5)), (0.05, tone(220, 0.4, 0.25, decay=8))))
    save_wav(OUT / "notify.wav", meow())
    save_wav(OUT / "connect.wav", tone(lambda t: 600 + 3600 * t, 0.3, decay=9))
    save_wav(OUT / "disconnect.wav", tone(lambda t: 1300 - 3600 * t, 0.3, decay=9))

    for f in sorted(OUT.iterdir()):
        print(f"{f.name:20} {f.stat().st_size:>9}")


if __name__ == "__main__":
    main()
