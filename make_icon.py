# -*- coding: utf-8 -*-
"""生成 zmd-orb 的图标：透明背景 + 电量环（灰衬环 + 淡黄轨道 + 亮黄进度弧）。

产物（orb/）：
    icon.ico（WPF 用 ApplicationIcon）/ 32x32.png / 128x128.png / 128x128@2x.png（留给安装包）

用法：python make_icon.py
"""
import os

from PIL import Image, ImageDraw

OUT = os.path.join("orb")
S = 256
GREY = (211, 211, 206, 255)   # 灰衬环 #d3d3ce
TRACK = (242, 237, 196, 255)  # 淡黄轨道 #f2edc4
YELLOW = (255, 226, 61, 255)  # 亮黄进度弧 #ffe23d


def render():
    """球 = 圆环；亮黄弧从 12 点起顺时针扫过约 250°（跟界面同一个口径）。"""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = 26
    box = [m, m, S - m, S - m]
    d.ellipse(box, outline=GREY, width=26)    # 衬环
    d.ellipse([m + 5, m + 5, S - m - 5, S - m - 5], outline=TRACK, width=18)  # 轨道
    # PIL 的角度以 3 点钟为 0、顺时针为正；-90 即 12 点
    d.arc([m + 5, m + 5, S - m - 5, S - m - 5], start=-90, end=160, fill=YELLOW, width=18)
    return img


def main():
    base = render()
    os.makedirs(OUT, exist_ok=True)
    for size, name in ((32, "32x32.png"), (128, "128x128.png"), (256, "128x128@2x.png")):
        base.resize((size, size), Image.LANCZOS).save(os.path.join(OUT, name))
    base.save(os.path.join(OUT, "icon.ico"),
              sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)],
              format="ICO")
    print("wrote icons ->", OUT)


if __name__ == "__main__":
    main()