# -*- coding: utf-8 -*-
"""用 PyInstaller 把采集端打包成单文件 backend.exe（纯本地 API 服务）。

产物与 WPF 主程序同目录，由壳启动时拉起（--no-gui）。
--windowed 是为了让按需提权拉起的那次实例不闪黑框。

用法（在 zmd-orb/ 目录下）：
    python build_exe.py
产物： dist/backend.exe
"""
import os

import PyInstaller.__main__

PyInstaller.__main__.run([
    "collector/speed_collector.py",
    "--name", "backend",
    "--onefile",
    "--windowed",
    "--noconfirm",
    "--clean",
    "--icon", os.path.join("orb", "icon.ico"),
    "--add-data", "frontend" + os.pathsep + "frontend",
    "--hidden-import", "psutil",
])