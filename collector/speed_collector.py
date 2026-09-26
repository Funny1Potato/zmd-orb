# -*- coding: utf-8 -*-
"""终末地加速球 —— 本地采集端（M0 骨架）

依赖：pip install psutil
运行：python collector/speed_collector.py [--no-gui | --selftest]
对外：http://127.0.0.1:8910/snapshot   内存/CPU 快照（JSON，带 CORS）
      http://127.0.0.1:8910/health     健康检查（壳启动前探活用：是我们的采集端就直接复用）
      http://127.0.0.1:8910/dev.html   顺带伺服 frontend/ 静态页（只是设计参考，调色用）

M0 只输出只读数据：物理内存 / 提交额度 / 系统缓存 / CPU。
M1 会加：POST /clean（三级整理缓存页）、POST /kill、GET /clean/result。
"""
import ctypes
import json
import os
import sys
import threading
import time
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import psutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # 仓库根（前端所在处）
PORT = 8910          # 避开 zmd-manager 的 8899，两个工具可同时运行
INTERVAL = 1.0       # 球是常驻的，1s 足够；进程列表留到面板打开时再采（M3）

state = {"snapshot": None}


def resource_path(rel):
    """冻结后资源位于 sys._MEIPASS；源码运行则位于仓库根目录。"""
    base = getattr(sys, "_MEIPASS", None)
    return os.path.join(base, rel) if base else os.path.join(ROOT, rel)


# ---------------- 内存状态（ctypes，不拉子进程） ----------------
class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


class PERFORMANCE_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("CommitTotal", ctypes.c_size_t),
        ("CommitLimit", ctypes.c_size_t),
        ("CommitPeak", ctypes.c_size_t),
        ("PhysicalTotal", ctypes.c_size_t),
        ("PhysicalAvailable", ctypes.c_size_t),
        ("SystemCache", ctypes.c_size_t),
        ("KernelTotal", ctypes.c_size_t),
        ("KernelPaged", ctypes.c_size_t),
        ("KernelNonpaged", ctypes.c_size_t),
        ("PageSize", ctypes.c_size_t),
        ("HandleCount", ctypes.c_ulong),
        ("ProcessCount", ctypes.c_ulong),
        ("ThreadCount", ctypes.c_ulong),
    ]


MB = 1048576.0


def read_memory():
    """Physical/commit 走 Win32，一个子进程都不起。

    注意口径：ullAvailPhys（= 可用内存）包含待命列表（文件缓存），所以"清完待命列表可用内存上升"
    并不代表多出了内存。别拿它当"内存告急"的判据——那是 free+zero（M1 加）。
    """
    ms = MEMORYSTATUSEX()
    ms.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)):
        return None
    pi = PERFORMANCE_INFORMATION()
    pi.cb = ctypes.sizeof(PERFORMANCE_INFORMATION)
    ok = ctypes.windll.psapi.GetPerformanceInfo(ctypes.byref(pi), pi.cb)
    out = {
        "total_mb": round(ms.ullTotalPhys / MB, 1),
        "avail_mb": round(ms.ullAvailPhys / MB, 1),
        "used_mb": round((ms.ullTotalPhys - ms.ullAvailPhys) / MB, 1),
        "pct": round(ms.dwMemoryLoad, 1),
    }
    if ok:
        ps = float(pi.PageSize or 4096)
        limit = pi.CommitLimit * ps / MB
        used = pi.CommitTotal * ps / MB
        out.update({
            "system_cache_mb": round(pi.SystemCache * ps / MB, 1),
            "committed_mb": round(used, 1),
            "commit_limit_mb": round(limit, 1),
            "commit_pct": round(used / limit * 100, 1) if limit else 0.0,
        })
    return out


def read_cpu():
    try:
        f = psutil.cpu_freq()
        freq = round((f.current or 0) / 1000.0, 2) if f else 0.0
    except Exception:
        freq = 0.0
    return {
        "util": round(psutil.cpu_percent(None), 1),
        "freq": freq,
        "threads": psutil.cpu_count(logical=True) or 0,
    }


def sampler():
    psutil.cpu_percent(None)                 # 预热
    while True:
        try:
            mem = read_memory()
            if mem is not None:
                state["snapshot"] = {
                    "ts": time.time(),
                    "interval": INTERVAL,
                    "live": True,
                    "mem": mem,
                    "cpu": read_cpu(),
                    "boot": round(time.time() - psutil.boot_time()),
                }
        except Exception:
            pass
        time.sleep(INTERVAL)


# ---------------- HTTP ----------------
STATIC = {"ball.html", "panel.html", "dev.html", "ring.js", "api.js"}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/health":
            self._send(200, json.dumps({"service": "zmd-orb-collector", "port": PORT,
                                        "ready": state.get("snapshot") is not None}))
            return
        if path.startswith("/snapshot"):
            snap = state.get("snapshot")
            if snap is None:
                self._send(503, json.dumps({"live": False, "error": "采样尚未就绪"}))
            else:
                self._send(200, json.dumps(snap, ensure_ascii=False))
            return
        name = "ball.html" if path in ("/", "/index.html") else path.lstrip("/")
        if name in STATIC:
            ctype = "text/javascript; charset=utf-8" if name.endswith(".js") else "text/html; charset=utf-8"
            try:
                with open(resource_path(os.path.join("frontend", name)), "rb") as f:
                    self._send(200, f.read(), ctype)
            except Exception:
                self._send(404, "frontend/%s 未找到" % name, "text/plain; charset=utf-8")
            return
        self._send(200, json.dumps({"service": "zmd-orb-collector",
                                    "endpoints": ["/health", "/snapshot"]}, ensure_ascii=False))

    def log_message(self, *a):
        pass


def use_utf8_stdio():
    """把 stdout/stderr 钉成 UTF-8。

    冻结成 backend.exe 后 stdout 是管道，编码按系统区域取——非中文区域（如 cp1252）编不出
    中文日志，print 会直接抛 UnicodeEncodeError 把采集端打死（CI 的英文 runner 上实测）。
    显式 reconfigure 能盖掉 PYTHONIOENCODING 等启动期设置。"""
    for s in (sys.stdout, sys.stderr):
        try:
            if s is not None:
                s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def log(msg):
    """日志绝不允许打死采集端：编不出来就降级成 ASCII 再丢一次。"""
    try:
        print(msg, flush=True)
    except Exception:
        try:
            print(str(msg).encode("ascii", "replace").decode("ascii"), flush=True)
        except Exception:
            pass


def _selftest():
    import urllib.request
    data = None
    for _ in range(20):
        try:
            with urllib.request.urlopen("http://127.0.0.1:%d/snapshot" % PORT, timeout=3) as r:
                if r.status == 200:
                    data = json.loads(r.read().decode("utf-8"))
                    break
        except Exception:
            pass
        time.sleep(0.4)
    if data is None:
        ok, msg = False, "err=snapshot 未就绪"
    else:
        mem = data.get("mem") or {}
        ok = bool(data.get("live")) and mem.get("total_mb", 0) > 0
        msg = "live=%s mem=%.1f/%.1fG %.1f%% commit=%.1f/%.1fG cpu=%.1f%%" % (
            data.get("live"), (mem.get("used_mb", 0)) / 1024, (mem.get("total_mb", 0)) / 1024,
            mem.get("pct", 0), mem.get("committed_mb", 0) / 1024, mem.get("commit_limit_mb", 0) / 1024,
            (data.get("cpu") or {}).get("util", 0))
    try:
        with open(os.path.join(os.environ.get("TEMP", "."), "zmd_orb_selftest.txt"),
                  "w", encoding="utf-8") as f:
            f.write("%s\n%s\n" % ("PASS" if ok else "FAIL", msg))
    except Exception:
        pass
    log("[采集端] selftest %s %s" % ("PASS" if ok else "FAIL", msg))


def main(gui=True, selftest=False):
    use_utf8_stdio()
    mem = read_memory() or {}
    log("[采集端] 内存 %.1f GB，当前占用 %.1f%%（提交 %.1f/%.1f GB）"
        % ((mem.get("total_mb", 0)) / 1024, mem.get("pct", 0),
           (mem.get("committed_mb", 0)) / 1024, (mem.get("commit_limit_mb", 0)) / 1024))
    threading.Thread(target=sampler, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    log("[采集端] 已启动： http://127.0.0.1:%d/ （Ctrl+C 退出）" % PORT)

    if selftest:
        time.sleep(2.5)
        _selftest()
        srv.shutdown()
        return

    if gui:
        log("[采集端] 打开浏览器页面： http://127.0.0.1:%d/ball.html" % PORT)
        webbrowser.open("http://127.0.0.1:%d/dev.html" % PORT)

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        log("[采集端] 已退出")
        srv.shutdown()


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="终末地加速球 - 本地采集端")
    ap.add_argument("--no-gui", action="store_true", help="仅启动服务，不打开界面")
    ap.add_argument("--selftest", action="store_true", help="启动后自测一次并退出")
    a = ap.parse_args()
    main(gui=not a.no_gui, selftest=a.selftest)