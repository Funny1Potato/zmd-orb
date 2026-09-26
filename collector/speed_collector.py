# -*- coding: utf-8 -*-
"""终末地加速球 —— 本地采集端 / 清理端

依赖：pip install psutil
运行：python collector/speed_collector.py [--no-gui | --selftest]
对外：http://127.0.0.1:8910/snapshot      内存/CPU 快照（JSON，带 CORS）
      http://127.0.0.1:8910/health        健康检查（壳启动前探活用：是我们的采集端就直接复用）
      http://127.0.0.1:8910/clean?tier=   三级整理缓存页（l1 免提权 / l2、l3 需管理员，按需提权）
      http://127.0.0.1:8910/clean/result  最近一次整理的结果
      http://127.0.0.1:8910/auto          自动整理的开关与状态（POST 改，GET 看）
      http://127.0.0.1:8910/dev.html      顺带伺服 frontend/ 静态页（只是设计参考，调色用）

三级档位（档位单调：深度包含轻度。口径见 README "它做什么、不做什么"）：
  l1  清本用户进程的工作集（**免提权**）：把活跃工作集换成可回收的待命页。
      实测 196 个进程 → 待命 +3.2GB、已修改 +1.7GB、占用率 63%→53%、available +3.0GB，
      但 free+zero 不变 —— 它不产生内存，只是把内存变成"随时可回收"。
  l2  l1 + 刷已修改页 + 清待命列表（**待命列表才是"缓存页"的大头，实例上 8~13GB**）
  l3  l2 的全系统版：全系统清工作集 + 清系统文件缓存 + 清低优先级待命（最狠，硬缺页代价最大）

实测的特权边界（2026-09-26 本机逐条核过）：
  免提权就能成功：K32EmptyWorkingSet（本用户 194/214 个进程）、NtSetSystemInformation(0x50, 3) 刷已修改页
  必须管理员：NtSetSystemInformation(0x50, 4/5) 清待命、 (0x50, 2) 全系统清工作集、SetSystemFileCacheSize
  无提权时它们返回 0xC0000061 STATUS_PRIVILEGE_NOT_HELD / GetLastError()=5，壳会按需弹一次 UAC。

进程结束（POST /kill）留到 M3 和面板进程表一起做——没有进程表它没处可用。

M2 加了：硬缺页率（PDH，进程内读 \Memory\Pages Input/sec）+ 自动整理。
自动整理**只做 l1**（免提权，永不弹 UAC），策略是"空闲+零页低于阈值就轻度整理"，
默认**关闭**（它不会让 free+zero 变多，只是把活跃工作集转成随时可回收的待命页，
却会让那些页下次访问硬缺页——所以默认不替你决定）。状态见 /auto。
"""
import ctypes
import ctypes.wintypes as wintypes
import json
import os
import platform
import re
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import psutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # 仓库根（前端所在处）
PORT = 8910          # 避开 zmd-manager 的 8899，两个工具可同时运行
INTERVAL = 1.0       # 球是常驻的，1s 足够；进程列表留到面板打开时再采（M3）
THROTTLE = 30.0      # 手动整理后的冷却（秒）；壳那边只是 UI 提示，真正拦截在这里
SETTLE = 1.0         # 整理后等列表稳定再测"后"
ELEVATE_WAIT = 90.0  # 等用户点 UAC 的最长时间（秒）

state = {"snapshot": None, "last_result": None, "last_clean": 0.0, "cleaning": False,
         "fault_rate": None, "auto": {}, "auto_checked": 0.0, "auto_last": 0.0,
         "auto_count": 0, "auto_reason": ""}
_fault = None        # PdhRate，main() 里创建（硬缺页率采样器）
ntdll = ctypes.WinDLL("ntdll")

SystemMemoryListInformation = 0x50
MEM_EMPTY_WORKING_SETS = 2
MEM_FLUSH_MODIFIED_LIST = 3
MEM_PURGE_STANDBY_LIST = 4
MEM_PURGE_LOW_PRIORITY_STANDBY = 5


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


class MEMORY_LIST_INFORMATION(ctypes.Structure):
    """NtQuerySystemInformation(SystemMemoryListInformation=0x50) 的返回。

    2026-09-26 本机实测：OS 要求 176 字节（22 个 ULONG_PTR），比 phnt 的 15 字段版长，
    所以尾部补 7 个占位。前 13 个字段逐条对得上 WMI 的 Win32_PerfFormattedData_PerfOS_Memory
    （背靠背采样 6 轮）：待命合计差 <2MB、已修改差 <50MB、Free+Zero 差在数十 MB 级。
    尾部字段含义不明（按页换算会超过物理内存，显然不是物理列表规模），一律不用。
    """
    _fields_ = [
        ("ZeroPageCount", ctypes.c_size_t),
        ("FreePageCount", ctypes.c_size_t),
        ("ModifiedPageCount", ctypes.c_size_t),
        ("ModifiedNoWritePageCount", ctypes.c_size_t),
        ("BadPageCount", ctypes.c_size_t),
        ("PageCountByPriority", ctypes.c_size_t * 8),   # 待命列表按优先级分 8 档
        ("RepurposedPages", ctypes.c_size_t),
        ("ModifiedPageCountPageFile", ctypes.c_size_t),
        ("_tail", ctypes.c_size_t * 7),
    ]


class SYSTEM_INFO(ctypes.Structure):
    _fields_ = [
        ("wProcessorArchitecture", ctypes.c_ushort),
        ("wReserved", ctypes.c_ushort),
        ("dwPageSize", ctypes.c_ulong),
        ("lpMinimumApplicationAddress", ctypes.c_void_p),
        ("lpMaximumApplicationAddress", ctypes.c_void_p),
        ("dwActiveProcessorMask", ctypes.c_size_t),
        ("dwNumberOfProcessors", ctypes.c_ulong),
        ("dwProcessorType", ctypes.c_ulong),
        ("dwAllocationGranularity", ctypes.c_ulong),
        ("wProcessorLevel", ctypes.c_ushort),
        ("wProcessorRevision", ctypes.c_ushort),
    ]


def page_size():
    si = SYSTEM_INFO()
    ctypes.windll.kernel32.GetSystemInfo(ctypes.byref(si))
    return float(si.dwPageSize or 4096)


def read_memory_lists():
    """"整理缓存页"到底整理掉了什么，看的就是这几个列表。

    口径提醒：available 把待命列表算作可用，所以清空待命列表后 available 几乎不动；
    真正会动的是 free+zero（上升）与 standby（下降）。
    """
    buf = MEMORY_LIST_INFORMATION()
    status = ntdll.NtQuerySystemInformation(SystemMemoryListInformation, ctypes.byref(buf),
                                            ctypes.sizeof(buf), None)
    if status < 0:                      # 查询失败（理论上不需要管理员，失败就当作没有）
        return {}
    ps = page_size()
    prio = [round(v * ps / MB, 1) for v in buf.PageCountByPriority]
    return {
        "free_zero_mb": round((buf.ZeroPageCount + buf.FreePageCount) * ps / MB, 1),
        "standby_mb": round(sum(buf.PageCountByPriority) * ps / MB, 1),
        "standby_priority_mb": prio,
        "modified_mb": round(buf.ModifiedPageCount * ps / MB, 1),
        "modified_nowrite_mb": round(buf.ModifiedNoWritePageCount * ps / MB, 1),
    }


def read_memory():
    """Physical/commit 走 Win32，一个子进程都不起。

    口径：ullAvailPhys（= 可用内存）包含待命列表（文件缓存），所以"清完待命列表可用内存上升"
    并不代表多出了内存，别拿 avail 当"内存告急"的判据——那是 free_zero_mb。
    真会打崩程序的是提交额度（commit_limit 与本机页面文件大小绑定）。
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
    out.update(read_memory_lists())
    return out


# ---- 硬缺页率：PDH，进程内读，不起子进程 ----
PDH_FMT_DOUBLE = 0x00000200

_pdh = ctypes.WinDLL("pdh")
_pdh.PdhOpenQueryW.argtypes = [ctypes.c_wchar_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_void_p)]
_pdh.PdhOpenQueryW.restype = ctypes.c_ulong
_pdh.PdhAddEnglishCounterW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_size_t,
                                       ctypes.POINTER(ctypes.c_void_p)]
_pdh.PdhAddEnglishCounterW.restype = ctypes.c_ulong
_pdh.PdhCollectQueryData.argtypes = [ctypes.c_void_p]
_pdh.PdhCollectQueryData.restype = ctypes.c_ulong
_pdh.PdhGetFormattedCounterValue.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                                             ctypes.POINTER(ctypes.c_ulong), ctypes.c_void_p]
_pdh.PdhGetFormattedCounterValue.restype = ctypes.c_ulong
_pdh.PdhCloseQuery.argtypes = [ctypes.c_void_p]
_pdh.PdhCloseQuery.restype = ctypes.c_ulong


class PDH_FMT_COUNTERVALUE(ctypes.Structure):
    _fields_ = [("CStatus", ctypes.c_ulong), ("doubleValue", ctypes.c_double)]


class PdhRate:
    """一个速率型性能计数器（默认硬缺页 \\Memory\\Pages Input/sec）。

    为什么用 PdhAddEnglishCounterW：中文系统的计数器路径是本地化的，直接传英文名会找不到计数
    器；这个 API（Vista+）认英文名，绕开本地化。速率型计数器要两次 CollectQueryData 才有值，
    所以第一次 sample() 返回 None。实测单次采样 0.14~0.38ms，比起 PowerShell 快三个数量级。
    """
    def __init__(self, path=r"\Memory\Pages Input/sec"):
        self.path, self.value, self.error = path, None, ""
        self.q, self.c = ctypes.c_void_p(), ctypes.c_void_p()
        if _pdh.PdhOpenQueryW(None, 0, ctypes.byref(self.q)) != 0:
            self.error, self.q = "PdhOpenQueryW 失败", None
            return
        rc = _pdh.PdhAddEnglishCounterW(self.q, path, 0, ctypes.byref(self.c))
        if rc != 0:
            self.error = "PdhAddEnglishCounterW 返回 %d" % rc
            _pdh.PdhCloseQuery(self.q)
            self.q = None
            return
        _pdh.PdhCollectQueryData(self.q)          # 建立基线

    def sample(self):
        if not self.q:
            return None
        if _pdh.PdhCollectQueryData(self.q) != 0:
            return self.value
        val = PDH_FMT_COUNTERVALUE()
        typ = ctypes.c_ulong(0)
        if _pdh.PdhGetFormattedCounterValue(self.c, PDH_FMT_DOUBLE,
                                            ctypes.byref(typ), ctypes.byref(val)) != 0:
            return self.value
        if val.CStatus != 0:                      # 0 = PDH_CSTATUS_VALID_DATA
            return self.value
        self.value = round(val.doubleValue, 1)
        return self.value

    def close(self):
        if self.q:
            _pdh.PdhCloseQuery(self.q)
            self.q = None


def read_cpu():
    """只用 psutil 的 CPU 快照（设备页的 CPU 行由 build_snapshot 组装，这里留给诊断）。"""
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


def sample_procs():
    """进程采样（含 CPU% 的两次采样差）。采样线程每 INTERVAL 调一次，/processes 直接吃缓存。

    两边各自算 Δ 会把"上一次采样"的窗口打乱，所以由采样线程统一负责。
    """
    rows, dt = list_processes()
    enrich(rows)
    state["procs"] = rows
    state["procs_dt"] = dt
    state["procs_ts"] = time.time()
    return rows


def top_apps(rows):
    """应用概况列表的子集：滤掉几乎不动的、按 CPU 倒序、条数上限来自配置（面板可改）。"""
    limit = int((state.get("auto") or {}).get("app_limit", PROC_LIMIT) or PROC_LIMIT)
    keep = [p for p in rows if p["pid"] != 0 and not (p["cpu"] < 0.5 and p["mem_mb"] < 40)]
    keep.sort(key=lambda p: (-p["cpu"], -p["mem_mb"]))
    return [{"pid": p["pid"], "name": p["name"], "exe": "",
             "mem": round(p["mem_mb"]), "cpu": p["cpu"],
             "display": p.get("display") or p["name"].replace(".exe", ""),
             "title": p.get("title") or ""} for p in keep[:limit]]


def build_snapshot(mem, rows):
    """快照：zmd-manager 的那套（cpu/gpu/mem/disks/net/procs）+ zmd-orb 自己的（整理相关）。"""
    stat = state.get("stat") or {}
    now = time.time()
    dt = max(INTERVAL, now - (state.get("_net_t") or now))
    state["_net_t"] = now

    # 网络速率：两次采样的字节差（与参考一致）
    net = psutil.net_io_counters()
    pn = state.get("_prev_net") or net
    state["_prev_net"] = net
    down = max(0.0, (net.bytes_recv - pn.bytes_recv) * 8 / 1e6 / dt)
    up = max(0.0, (net.bytes_sent - pn.bytes_sent) * 8 / 1e6 / dt)
    link = stat.get("net_link", 1000) or 1000

    try:
        f = psutil.cpu_freq()
        freq = round((f.current or 0) / 1000.0, 2) if f else 0.0
    except Exception:
        freq = 0.0
    cpu_util = psutil.cpu_percent(None)

    # 硬盘：占用率优先用性能计数器，读写速率用计数器或 psutil 字节差兜底
    try:
        cur_disk = psutil.disk_io_counters(perdisk=True) or {}
    except Exception:
        cur_disk = {}
    prev_disk = state.get("_prev_disk") or {}
    state["_prev_disk"] = cur_disk
    disks = []
    for i, d in enumerate(stat.get("disks", [])):
        try:
            u = psutil.disk_usage(d["mount"])
        except Exception:
            continue
        io = (state.get("disk_io") or {}).get(d["letter"], {})
        active = io.get("% Disk Time")
        rw = ((io.get("Disk Read Bytes/sec") or 0) + (io.get("Disk Write Bytes/sec") or 0)) / MB
        if rw <= 0 and d.get("phys") in cur_disk and d["phys"] in prev_disk:
            a, b = prev_disk[d["phys"]], cur_disk[d["phys"]]
            rw = ((b.read_bytes - a.read_bytes) + (b.write_bytes - a.write_bytes)) / dt / MB
        disks.append({
            "name": "磁盘 %d (%s)" % (i, d["letter"]),
            "used": round(u.used / 2 ** 30, 1), "total": round(u.total / 2 ** 30, 1),
            "pct": round(u.percent, 1),
            "util": round(active, 1) if active is not None else round(u.percent, 1),
            "rw": "%.0f MB/s" % rw, "media": d["media"], "model": d["model"],
        })

    g = state.get("gpu") or {}
    vm = psutil.virtual_memory()
    return {
        "ts": now, "interval": INTERVAL, "live": True,
        "admin": is_admin(),                       # 壳/面板据此提示"深度整理需要管理员"
        "hard_fault_rate": state.get("fault_rate"),
        "auto": auto_status(),
        "cpu": {"name": stat.get("cpu_name"), "threads": stat.get("cpu_threads"),
                "util": round(cpu_util, 1), "freq": freq,
                "base": stat.get("cpu_base"), "max": stat.get("cpu_max")},
        "gpu": {"name": stat.get("gpu_name"), "util": round(g.get("util", 0), 1),
                "freq": g.get("freq"), "mem_used": g.get("mem_used"),
                "mem_total": stat.get("gpu_mem_total"), "ok": bool(g.get("ok"))},
        "mem": dict(mem,
                    used=round(vm.used / 2 ** 30, 1), total=round(vm.total / 2 ** 30, 1),
                    pct=round(vm.percent, 1), speed=stat.get("mem_speed"),
                    type=stat.get("mem_type")),
        "disks": disks,
        "net": {"name": stat.get("net_name"), "down": round(down, 2), "up": round(up, 2),
                "link": link, "util": round(min(100.0, max(down, up) / link * 100), 1)},
        "procs": top_apps(rows),
    }


def sampler():
    psutil.cpu_percent(None)                 # 预热
    while True:
        try:
            mem = read_memory() or {}
            if _fault is not None:
                state["fault_rate"] = _fault.sample()
            rows = sample_procs()
            state["snapshot"] = build_snapshot(mem, rows)
            auto_tick()                       # 自动整理（默认关；只做免提权的 l1）
        except Exception as e:
            log("[采集端] 采样异常：%s" % e)
        time.sleep(INTERVAL)


# ---------------- 整理（M1） ----------------
# 进程工作集相关的 prototype 必须显式声明：OpenProcess 返回的是 64 位句柄，
# 让 ctypes 按默认的 c_int 收会被截断。
_k32 = ctypes.windll.kernel32
_k32.OpenProcess.restype = ctypes.c_void_p
_k32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
_k32.CloseHandle.argtypes = [ctypes.c_void_p]
if hasattr(_k32, "K32EmptyWorkingSet"):
    _k32.K32EmptyWorkingSet.restype = ctypes.c_int
    _k32.K32EmptyWorkingSet.argtypes = [ctypes.c_void_p]
_k32.GetCurrentProcess.restype = ctypes.c_void_p
_k32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
_k32.WaitForSingleObject.restype = ctypes.c_ulong
_k32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
_k32.GetExitCodeProcess.restype = ctypes.c_int


def app_dir():
    """采集端自己所在的目录（提权拉起时当工作目录用；路径都是绝对的，只是别让 cwd 落在 system32）。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))

PROCESS_SET_QUOTA = 0x0100
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
SW_SHOWNORMAL = 1

# 默认不动这些进程：清自己没意义（还会把刚画好的界面清掉），系统关键进程本来也打不开，
# 写出来是为了日志里能看出"是有意跳过的"。
DEFAULT_EXCLUDE = {
    "zmd-orb.exe", "backend.exe", "python.exe", "pythonw.exe",
    # Memory Compression 与 memcompression 是同一个进程的两种叫法：
    # 我们用 NtQuery 的 ImageName（Memory Compression），psutil 回退路径给的是 memcompression
    "system", "registry", "memory compression", "memcompression", "idle",
    "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe",
    "winlogon.exe", "dwm.exe", "fontdrvhost.exe", "audiodg.exe", "sihost.exe",
}


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def config_path():
    """排除名单：先看 %LOCALAPPDATA%/zmd-orb/exclude.txt，再看程序同目录。"""
    p = os.path.join(os.environ.get("LOCALAPPDATA", "."), "zmd-orb", "exclude.txt")
    if os.path.exists(p):
        return p
    alt = resource_path("exclude.txt")
    return alt if os.path.exists(alt) else p


def load_exclusions():
    """默认名单 + 可选 exclude.txt：每行一个进程名，# 注释，!name 表示从默认名单里去掉，
    foreground_exclude=0 可关掉"不清前台进程"的保护。"""
    names = set(DEFAULT_EXCLUDE)
    skip_fg = True
    p = config_path()
    if os.path.exists(p):
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                for line in f:
                    s = line.strip()
                    if not s or s.startswith("#"):
                        continue
                    if s.startswith("!"):
                        names.discard(s[1:].strip().lower())
                    elif s.lower().startswith("foreground_exclude="):
                        skip_fg = s.split("=", 1)[1].strip().lower() not in ("0", "false", "no")
                    else:
                        names.add(s.lower())
        except Exception as e:
            log("[清理端] 读排除名单失败：%s" % e)
    return names, skip_fg


def foreground_process_name():
    u = ctypes.windll.user32
    h = u.GetForegroundWindow()
    if not h:
        return ""
    pid = wintypes.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    try:
        return (psutil.Process(pid.value).name() or "").lower()
    except Exception:
        return ""


# ---- 进程枚举：一次系统调用拿全，别用 psutil（慢两个数量级） ----
SystemProcessInformation = 5


class UNICODE_STRING(ctypes.Structure):
    _fields_ = [("Length", ctypes.c_ushort), ("MaximumLength", ctypes.c_ushort),
                ("Buffer", ctypes.c_void_p)]


class SYSTEM_PROCESS_INFORMATION(ctypes.Structure):
    """NtQuerySystemInformation(SystemProcessInformation) 的一条记录。

    这里只用 ImageName / UniqueProcessId，后面的字段不声明（靠 NextEntryOffset 走链）。
    实测（2026-09-26 本机）：一次调用拿全 403 个进程的 pid+名字约 5ms；而
    psutil.process_iter(["pid","name"]) 要 2114ms —— 它每个进程都要 OpenProcess 取名字，
    比"清工作集"本身（175ms）还慢十倍，所以枚举自己来。M3 的进程表也用这条链。
    """
    _fields_ = [
        ("NextEntryOffset", ctypes.c_ulong),
        ("NumberOfThreads", ctypes.c_ulong),
        ("WorkingSetPrivateSize", ctypes.c_longlong),
        ("HardFaultCount", ctypes.c_ulong),
        ("NumberOfThreadsHighWatermark", ctypes.c_ulong),
        ("CycleTime", ctypes.c_ulonglong),
        ("CreateTime", ctypes.c_longlong),
        ("UserTime", ctypes.c_longlong),
        ("KernelTime", ctypes.c_longlong),
        ("ImageName", UNICODE_STRING),
        ("BasePriority", ctypes.c_long),
        ("UniqueProcessId", ctypes.c_void_p),
        ("InheritedFromUniqueProcessId", ctypes.c_void_p),
    ]


# 已用 psutil 逐条对出来的字段偏移（base = 记录起始地址，x64）：
#   4   NumberOfThreads(u32)      40  UserTime(i64, 100ns)     48  KernelTime(i64, 100ns)
#   56  ImageName(UNICODE_STRING) 80  UniqueProcessId(vp)      88  InheritedFromUniqueProcessId(vp)
#   144 WorkingSetSize（字节；u64 与 u32 读出来一样，取 u64 并对"大于物理内存"做回绕兜底）
# 其余字段（内存列表那种"猜结构体"的坑）不声明、不读。
OFF_THREADS, OFF_USER, OFF_KERN = 4, 40, 48
OFF_IMG, OFF_PID, OFF_PPID, OFF_WS = 56, 80, 88, 144


def enumerate_processes(phys_bytes=0):
    """一次系统调用拿全：[{pid,name,ppid,threads,user,kern,ws}]。

    实测（2026-09-26）：403 个进程约 17ms。而 psutil.process_iter(["pid","name"])
    要 2114ms（它每个进程都得 OpenProcess 取名字），比"清工作集"本身还慢十倍。
    """
    size = 512 * 1024
    for _ in range(6):
        buf = ctypes.create_string_buffer(size)
        ret = ctypes.c_ulong(0)
        status = ntdll.NtQuerySystemInformation(SystemProcessInformation, buf, size,
                                                ctypes.byref(ret))
        if (status & 0xFFFFFFFF) == 0xC0000004:          # STATUS_INFO_LENGTH_MISMATCH
            size = max(size * 2, ret.value + 64 * 1024)
            continue
        if status < 0:
            return []
        out, off = [], 0
        while True:
            base = ctypes.addressof(buf) + off
            nxt = ctypes.c_ulong.from_address(base).value
            pid = ctypes.c_void_p.from_address(base + OFF_PID).value or 0
            if pid:
                img_len = ctypes.c_ushort.from_address(base + OFF_IMG).value
                img_buf = ctypes.c_void_p.from_address(base + OFF_IMG + 8).value
                name = ctypes.wstring_at(img_buf, img_len // 2).lower() if (img_buf and img_len) else ""
                ws = int.from_bytes(ctypes.string_at(base + OFF_WS, 8), "little")
                if phys_bytes and ws > phys_bytes:       # 内核字段若是 u32，>4GB 会回绕
                    ws = int.from_bytes(ctypes.string_at(base + OFF_WS, 4), "little")
                out.append({
                    "pid": pid, "name": name,
                    "ppid": ctypes.c_void_p.from_address(base + OFF_PPID).value or 0,
                    "threads": ctypes.c_ulong.from_address(base + OFF_THREADS).value,
                    "user": ctypes.c_longlong.from_address(base + OFF_USER).value,
                    "kern": ctypes.c_longlong.from_address(base + OFF_KERN).value,
                    "ws": ws,
                })
            if not nxt:
                break
            off += nxt
        return out
    return []


def empty_working_sets(exclude, skip_fg=True):
    """清进程工作集。

    无提权也覆盖绝大多数应用进程（本次实测本用户 214 个里能打开 194 个；打不开的基本是
    SYSTEM/受保护进程）。正在用的前台程序不清——它一被清就有可见卡顿，得不偿失。
    """
    fg = foreground_process_name() if skip_fg else ""
    procs = enumerate_processes()
    if not procs:                                  # 枚举失败才退回 psutil（慢但能用）
        for p in psutil.process_iter(["pid", "name"]):
            try:
                procs.append({"pid": p.info["pid"], "name": (p.info.get("name") or "").lower()})
            except Exception:
                pass
    done = skipped = failed = 0
    for p in procs:
        pid, name = p["pid"], p["name"]
        if not name or name in exclude or (fg and name == fg):
            skipped += 1
            continue
        h = _k32.OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not h:
            failed += 1
            continue
        try:
            if _k32.K32EmptyWorkingSet(h):
                done += 1
            else:
                failed += 1
        except Exception:
            failed += 1
        finally:
            _k32.CloseHandle(h)
    return {"processes": done, "skipped": skipped, "failed": failed}


# ---- 特权：NtSetSystemInformation 需要，且 AdjustTokenPrivileges 的坑很多 ----
SE_PRIVILEGE_ENABLED = 0x00000002
TOKEN_ADJUST_PRIVILEGES = 0x0020
TOKEN_QUERY = 0x0008
ERROR_NOT_ALL_ASSIGNED = 1300


class LUID(ctypes.Structure):
    _fields_ = [("LowPart", ctypes.c_ulong), ("HighPart", ctypes.c_long)]


class LUID_AND_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Luid", LUID), ("Attributes", ctypes.c_ulong)]


class TOKEN_PRIVILEGES(ctypes.Structure):
    _fields_ = [("PrivilegeCount", ctypes.c_ulong), ("Privileges", LUID_AND_ATTRIBUTES * 8)]


# advapi32 这几个也要显式声明：不声明的话句柄参数会被当成 c_int 收，
# 64 位句柄直接 OverflowError（"int too long to convert"）。
_adv = ctypes.windll.advapi32
_adv.OpenProcessToken.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_void_p)]
_adv.OpenProcessToken.restype = ctypes.c_int
_adv.LookupPrivilegeValueW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.POINTER(LUID)]
_adv.LookupPrivilegeValueW.restype = ctypes.c_int
_adv.AdjustTokenPrivileges.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                                       ctypes.c_ulong, ctypes.c_void_p, ctypes.c_void_p]
_adv.AdjustTokenPrivileges.restype = ctypes.c_int


def enable_privileges(names):
    """尽量启用特权，返回实际拿到的名单。

    坑：AdjustTokenPrivileges 即使一个特权都没启用也返回 TRUE，必须再看 GetLastError：
    1300 = ERROR_NOT_ALL_ASSIGNED（"并非所有被引用的特权或组都分配给呼叫方"）。
    """
    token = ctypes.c_void_p()
    if not _adv.OpenProcessToken(_k32.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                                 ctypes.byref(token)):
        return []
    try:
        tp = TOKEN_PRIVILEGES()
        got = []
        for name in names:
            luid = LUID()
            if not _adv.LookupPrivilegeValueW(None, name, ctypes.byref(luid)):
                continue
            tp.Privileges[len(got)].Luid = luid
            tp.Privileges[len(got)].Attributes = SE_PRIVILEGE_ENABLED
            got.append(name)
        if not got:
            return []
        tp.PrivilegeCount = len(got)
        _k32.SetLastError(0)
        ok = _adv.AdjustTokenPrivileges(token, False, ctypes.byref(tp), 0, None, None)
        err = _k32.GetLastError()
        if ok and err != ERROR_NOT_ALL_ASSIGNED:
            return got
        return []
    finally:
        _k32.CloseHandle(token)


def ntset(cmd):
    """NtSetSystemInformation(SystemMemoryListInformation, cmd)。

    返回 NTSTATUS（不设 GetLastError），成功按 NT_SUCCESS 语义判（status >= 0）。
    无提权实测 (0x50, 4) → 0xC0000061 STATUS_PRIVILEGE_NOT_HELD。
    """
    arg = ctypes.c_ulong(cmd)
    return ntdll.NtSetSystemInformation(SystemMemoryListInformation, ctypes.byref(arg),
                                        ctypes.sizeof(arg))


def nt_text(status):
    if status >= 0:
        return "OK"
    code = status & 0xFFFFFFFF
    if code == 0xC0000061:
        return "需要管理员（STATUS_PRIVILEGE_NOT_HELD）"
    if code == 0xC0000022:
        return "拒绝访问"
    return "失败 0x%08X" % code


def purge_file_cache():
    """SetSystemFileCacheSize(SIZE_T(-1), SIZE_T(-1), 0)：把系统文件缓存上限放开，
    等价于让它把缓存的页倒掉（RAMMap 的 Empty System Working Set 就是这个）。

    坑：与本文件其它 API 相反——无提权时它返回 0 且 GetLastError()=5（拒绝访问），
    所以"成功"的判定是"返回非 0 且 GetLastError 为 0"。
    """
    _k32.SetLastError(0)
    ok = _k32.SetSystemFileCacheSize(ctypes.c_size_t(-1), ctypes.c_size_t(-1), 0)
    err = _k32.GetLastError()
    if ok and err == 0:
        return "OK", True
    if err == 5:
        return "需要管理员（拒绝访问）", False
    return "失败 err=%d" % err, False


def step(steps, name, result, ok, detail=""):
    steps.append({"step": name, "result": result, "ok": bool(ok), "detail": detail})


def clean_l1(steps):
    ex, skip_fg = load_exclusions()
    t0 = time.perf_counter()
    r = empty_working_sets(ex, skip_fg)
    step(steps, "清进程工作集", "OK",
         r["processes"] > 0,
         "%d 个进程（跳过 %d，打不开 %d）· %dms"
         % (r["processes"], r["skipped"], r["failed"], round((time.perf_counter() - t0) * 1000)))
    return r["processes"]


def clean_l2(steps):
    st = ntset(MEM_FLUSH_MODIFIED_LIST)
    step(steps, "刷已修改页到 pagefile", nt_text(st), st >= 0)
    st = ntset(MEM_PURGE_STANDBY_LIST)
    step(steps, "清待命列表（缓存页）", nt_text(st), st >= 0)


def clean_l3(steps):
    st = ntset(MEM_EMPTY_WORKING_SETS)
    step(steps, "全系统清工作集", nt_text(st), st >= 0)
    r, ok = purge_file_cache()
    step(steps, "清系统文件缓存", r, ok)
    clean_l2(steps)
    st = ntset(MEM_PURGE_LOW_PRIORITY_STANDBY)
    step(steps, "清低优先级待命", nt_text(st), st >= 0)


def measure():
    m = read_memory() or {}
    # 硬缺页率是速率型指标，取不到瞬时值，只能用采样线程最近那次（1 秒一次）
    m["hard_fault_rate"] = state.get("fault_rate")
    return m


def delta(before, after, key):
    b, a = before.get(key), after.get(key)
    if isinstance(b, (int, float)) and isinstance(a, (int, float)):
        return round(a - b, 1)
    return None


def summarize(res):
    """分档如实说话：l1 换出去的是"工作集"，不是"缓存页"，而且 free+zero 不会变好。
    实测（2026-09-26 本机）：l1 清 196 个进程后 待命 +3.2GB、已修改 +1.7GB、system cache +2.4GB、
    占用率 63%→53%、available +3.0GB，而 free+zero −0.18GB —— 也就是说"占用率/可用内存变好看"
    并不代表多出了内存。这是本工具最要紧的一条口径，别在文案里含糊过去。"""
    d = res.get("delta") or {}
    gb = lambda v: (v or 0) / 1024.0
    tier = res.get("tier")
    if tier == "l1":
        moved = gb(d.get("standby_mb")) + gb(d.get("modified_mb"))
        return {
            "summary": "换出 %.1f GB 工作集 · %d 进程" % (moved, res.get("ws_processes") or 0),
            "detail": "%d 个进程的工作集被换进待命/已修改列表 %.1f GB（待命 %+.1f、已修改 %+.1f）；"
                      "free+zero %+.2f GB —— 可回收，但下次访问要硬缺页从 pagefile 读回，"
                      "并没有真的多出内存。" % (
                          res.get("ws_processes") or 0, moved,
                          gb(d.get("standby_mb")), gb(d.get("modified_mb")), gb(d.get("free_zero_mb"))),
        }
    purged = -gb(d.get("standby_mb"))
    freed = gb(d.get("free_zero_mb"))
    parts = ["清掉待命缓存页 %.1f GB" % max(0.0, purged)]
    if abs(gb(d.get("system_cache_mb"))) > 51:
        parts.append("文件缓存 %+.1f GB" % gb(d.get("system_cache_mb")))
    if abs(gb(d.get("modified_mb"))) > 51:
        parts.append("已修改页 %+.1f GB" % gb(d.get("modified_mb")))
    if abs(gb(d.get("committed_mb"))) > 51:
        parts.append("提交 %+.1f GB" % gb(d.get("committed_mb")))
    return {
        "summary": "整理 %.1f GB 缓存页" % max(0.0, purged),
        "detail": "、".join(parts) + "；free+zero %+.1f GB（待命列表本来就被 available 算作可用，"
                                   "所以别拿可用内存看效果）。" % freed,
    }


class SHELLEXECUTEINFOW(ctypes.Structure):
    """ShellExecuteExW 的入参/出参。

    用它而不是 ShellExecuteW：带上 SEE_MASK_NOCLOSEPROCESS 能拿到提权进程的句柄，
    于是"helper 起来就崩 / 参数不认识直接退出"能立刻报出来，而不是干等超时。
    （2026-09-26 踩过：命令行多了一个 argparse 不认识的 --force，提权进程当场退出，
    父进程只能等满 90 秒才报"超时"，完全看不出真因。）
    """
    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("fMask", ctypes.c_ulong),
        ("hwnd", ctypes.c_void_p),
        ("lpVerb", ctypes.c_wchar_p),
        ("lpFile", ctypes.c_wchar_p),
        ("lpParameters", ctypes.c_wchar_p),
        ("lpDirectory", ctypes.c_wchar_p),
        ("nShow", ctypes.c_int),
        ("hInstApp", ctypes.c_void_p),
        ("lpIDList", ctypes.c_void_p),
        ("lpClass", ctypes.c_wchar_p),
        ("hkeyClass", ctypes.c_void_p),
        ("dwHotKey", ctypes.c_ulong),
        ("hIconOrMonitor", ctypes.c_void_p),
        ("hProcess", ctypes.c_void_p),
    ]


SEE_MASK_NOCLOSEPROCESS = 0x00000040
WAIT_OBJECT_0 = 0x00000000
ERROR_CANCELLED = 1223


def run_elevated(tier):
    """按需提权做一次整理（helper 模式：--clean-now）。"""
    res = launch_elevated("--clean-now %s" % tier, "整理 " + tier)
    res.setdefault("tier", tier)
    state["last_result"] = res
    if res.get("ok"):
        state["last_clean"] = time.time()
    return res


def launch_elevated(helper_args, label):
    """用 runas 把采集端自己再拉一份，做完把结果（helper 写的 JSON）取回来。

    只负责"拉起 + 等 + 取结果 + 记日志"，状态由调用方写——免得整理和结束进程互相污染状态。

    用 ShellExecuteExW 而不是 ShellExecuteW：带上 SEE_MASK_NOCLOSEPROCESS 能拿到提权进程句柄，
    于是"helper 一起来就崩 / 参数不认识直接退出"能立刻按退出码报出来，而不是干等超时
    （2026-09-26 踩过：命令行多了一个 argparse 不认识的 --force，白等 90 秒）。
    """
    out = os.path.join(tempfile.gettempdir(), "zmd_orb_helper_%d.json" % os.getpid())
    try:
        if os.path.exists(out):
            os.remove(out)
    except OSError:
        pass

    if getattr(sys, "frozen", False):
        exe, params = sys.executable, '%s --out "%s"' % (helper_args, out)
    else:
        exe = sys.executable
        params = '"%s" %s --out "%s"' % (os.path.abspath(__file__), helper_args, out)
    log("[清理端] 提权拉起：%s %s" % (exe, params))

    sei = SHELLEXECUTEINFOW()
    sei.cbSize = ctypes.sizeof(sei)
    sei.fMask = SEE_MASK_NOCLOSEPROCESS
    sei.lpVerb = "runas"
    sei.lpFile = exe
    sei.lpParameters = params
    sei.lpDirectory = app_dir()
    sei.nShow = SW_SHOWNORMAL
    _k32.SetLastError(0)
    ok = ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(sei))
    if not ok:
        err = _k32.GetLastError()
        msg = "你取消了管理员授权" if err == ERROR_CANCELLED else "提权失败（GetLastError=%d）" % err
        log("[清理端] %s 未执行：%s" % (label, msg))
        return {"ok": False, "error": msg, "need_admin": True, "ts": time.time()}

    proc = sei.hProcess
    try:
        deadline = time.time() + ELEVATE_WAIT
        while time.time() < deadline:
            if os.path.exists(out):
                return _read_helper_result(label, out)
            # 每次等 300ms：既能及时发现进程退出，也不至于空转
            if proc and _k32.WaitForSingleObject(proc, 300) == WAIT_OBJECT_0:
                if os.path.exists(out):
                    return _read_helper_result(label, out)
                code = ctypes.c_ulong(0)
                _k32.GetExitCodeProcess(proc, ctypes.byref(code))
                msg = ("提权 helper 直接退出了（退出码 %d）且没写出结果；"
                       "多半是命令行参数有问题，看壳日志里那行'提权拉起'" % code.value)
                log("[清理端] %s 失败：%s" % (label, msg))
                return {"ok": False, "error": msg, "need_admin": True, "ts": time.time()}
    finally:
        if proc:
            _k32.CloseHandle(proc)

    log("[清理端] %s 失败：提权后的操作超时（等了 %d 秒）" % (label, ELEVATE_WAIT))
    return {"ok": False, "error": "提权后的操作超时（等了 %d 秒）" % ELEVATE_WAIT, "need_admin": True}


def _read_helper_result(label, out):
    try:
        with open(out, encoding="utf-8") as f:
            res = json.load(f)
    except Exception as e:
        res = {"ok": False, "error": "读不到提权结果：%s" % e}
    try:
        os.remove(out)
    except OSError:
        pass
    log("[清理端] %s 完成：%s" % (label, res.get("summary") or res.get("error") or res))
    return res


def do_clean(tier="l1", force=False):
    tier = (tier or "l1").lower()
    if tier not in ("l1", "l2", "l3"):
        return {"ok": False, "error": "未知档位 %s（可用 l1 / l2 / l3）" % tier}
    if state["cleaning"]:
        return {"ok": False, "error": "上一次整理还没结束"}

    left = THROTTLE - (time.time() - state["last_clean"])
    if not force and left > 0:
        return {"ok": False, "error": "冷却中", "retry_after": round(left, 1)}

    admin = is_admin()
    if tier in ("l2", "l3") and not admin:
        state["cleaning"] = True
        try:
            return run_elevated(tier)
        finally:
            state["cleaning"] = False

    state["cleaning"] = True
    try:
        steps = []
        if tier in ("l2", "l3"):
            got = enable_privileges(["SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege"])
            step(steps, "启用特权", "OK" if got else "未全部拿到",
                 bool(got), "、".join(got) if got else "一个都没拿到")
        before = measure()
        ws = 0
        t0 = time.perf_counter()
        if tier == "l1":
            ws = clean_l1(steps)
        elif tier == "l2":
            ws = clean_l1(steps)      # 档位单调：深度必然包含轻度
            clean_l2(steps)
        else:
            clean_l3(steps)
        run_ms = (time.perf_counter() - t0) * 1000
        t1 = time.perf_counter()
        time.sleep(SETTLE)
        after = measure()
        settle_ms = (time.perf_counter() - t1) * 1000
        res = {
            "ok": all(s["ok"] for s in steps) if steps else False,
            "tier": tier, "admin": admin, "ts": time.time(), "steps": steps,
            "before": before, "after": after, "ws_processes": ws,
            "timing": {"run_ms": round(run_ms), "settle_ms": round(settle_ms),
                       "total_ms": round(run_ms + settle_ms)},
            "delta": {k: delta(before, after, k) for k in
                      ("free_zero_mb", "standby_mb", "modified_mb",
                       "system_cache_mb", "committed_mb", "avail_mb", "hard_fault_rate")},
        }
        s = summarize(res)
        res["summary"] = s["summary"]
        res["detail"] = s["detail"]
        state["last_result"] = res
        if res["ok"]:
            state["last_clean"] = time.time()
        log("[清理端] %s → %s（执行 %dms + 等稳定 %dms）"
            % (tier, s["summary"], round(run_ms), round(settle_ms)))
        return res
    finally:
        state["cleaning"] = False


# ---------------- 进程表与结束进程（M3） ----------------
PROCESS_TERMINATE = 0x0001
ERROR_ACCESS_DENIED = 5

# 拦住这些：杀它们的后果不是"某个程序关掉"，而是整个系统完蛋
KILL_GUARD = {
    "system", "registry", "memory compression", "memcompression", "idle", "secure system",
    "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe",
    "fontdrvhost.exe",
}
_pcpu = {"ts": 0.0, "by_pid": {}}     # 上一次的 CPU 时间快照（算 CPU% 要两次采样）


def list_processes():
    """进程表：[{pid,name,ppid,threads,mem_mb,cpu,guarded}]，按内存倒序。

    CPU% 用"上一次调用到这一次"的 CPU 时间差算，所以**第一次调用全是 0**
    （Task Manager 第一次打开也是这样）。名字/内存/时间都来自一次 NtQuery，约 17ms。
    """
    now = time.perf_counter()
    procs = enumerate_processes(phys_bytes=state.get("phys_bytes", 0))
    prev = _pcpu["by_pid"]
    dt = now - _pcpu["ts"] if _pcpu["ts"] else 0.0
    ncpu = psutil.cpu_count(logical=True) or 1
    rows = []
    for p in procs:
        cpu = 0.0
        if dt > 0.2 and p["pid"] in prev:
            d = (p["user"] + p["kern"]) - prev[p["pid"]]
            cpu = max(0.0, d / 1e7 / dt / ncpu * 100.0)      # 100ns → 秒，再按逻辑核数归一
        rows.append({
            "pid": p["pid"], "name": p["name"], "ppid": p["ppid"],
            "threads": p["threads"], "mem_mb": round(p["ws"] / MB, 1), "cpu": round(cpu, 1),
            "guarded": p["name"] in KILL_GUARD or p["pid"] <= 4,
        })
    _pcpu["ts"] = now
    _pcpu["by_pid"] = {p["pid"]: p["user"] + p["kern"] for p in procs}
    rows.sort(key=lambda r: -r["mem_mb"])
    return rows, round(dt, 2)


def _ancestors(pid, by_pid):
    seen, cur = set(), pid
    while cur and cur not in seen:
        seen.add(cur)
        cur = by_pid.get(cur, {}).get("ppid", 0)
    return seen


def kill_one(pid):
    """结束一个进程。返回 (是否成功, 失败原因/错误码)。"""
    _k32.SetLastError(0)
    h = _k32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not h:
        err = _k32.GetLastError()
        return False, err
    try:
        if _k32.TerminateProcess(h, 1):
            return True, 0
        return False, _k32.GetLastError()
    finally:
        _k32.CloseHandle(h)


def _kill_local(pid, tree, procs):
    by_pid = {p["pid"]: p for p in procs}
    t = by_pid.get(pid)
    if t is None:
        return {"ok": False, "pid": pid, "error": "pid %d 已经不存在了" % pid}
    if t["pid"] <= 4 or t["name"] in KILL_GUARD:
        return {"ok": False, "pid": pid, "name": t["name"],
                "error": "%s（pid %d）是内核/关键进程，不动它" % (t["name"] or "?", pid)}
    protect = _ancestors(os.getpid(), by_pid)          # 采集端自己 + 壳（父进程链）
    if pid in protect:
        return {"ok": False, "pid": pid, "name": t["name"],
                "error": "那是本工具自己的进程（要退出请用面板右上角的 ✕）"}

    targets = [pid]
    if tree:                                           # 子孙先死，父后死
        kids = {}
        for p in procs:
            kids.setdefault(p["ppid"], []).append(p["pid"])
        stack, order = [pid], []
        while stack:
            cur = stack.pop()
            order.append(cur)
            stack.extend(kids.get(cur, []))
        targets = list(reversed(order))
    targets = [p for p in targets if p not in protect]

    killed, failed = [], []
    for p in targets:
        ok, err = kill_one(p)
        if ok:
            killed.append(p)
        else:
            failed.append({"pid": p, "err": err})
    res = {"ok": pid in killed, "pid": pid, "name": t["name"], "tree": bool(tree),
           "killed": killed, "failed": failed}
    if res["ok"]:
        res["summary"] = "已结束 %s（pid %d）%s" % (
            t["name"], pid, "，连 %d 个子孙进程" % (len(killed) - 1) if tree and len(killed) > 1 else "")
    else:
        first = failed[0] if failed else {"err": 0}
        res["error"] = ("没能结束 %s（pid %d）：%s" % (
            t["name"], pid,
            "权限不足（需要管理员）" if first["err"] == ERROR_ACCESS_DENIED
            else "错误码 %d" % first["err"]))
        res["denied"] = first["err"] == ERROR_ACCESS_DENIED
    return res


def kill_process(pid, tree=False):
    """结束进程；被拒绝且非管理员时，按需提权再试一次（与清理用同一套 helper 机制）。"""
    procs = enumerate_processes()
    res = _kill_local(pid, tree, procs)
    if res.get("ok") or not res.get("denied") or is_admin():
        log("[清理端] 结束进程 %s → %s" % (pid, res.get("summary") or res.get("error")))
        return res

    log("[清理端] 结束 %s 权限不足，改为提权重试" % pid)
    ev = launch_elevated("--kill-now %d%s" % (pid, " --tree" if tree else ""), "结束 %s" % pid)
    state["kill_result"] = ev
    return ev


# ---------------- 自动整理（M2）+ 显示设置（M4） ----------------
AUTO_DEFAULTS = {"enabled": False, "threshold_mb": 2048, "check_secs": 60, "min_gap_secs": 180,
                 "app_limit": 40}       # app_limit：应用概况列表最多几条（面板上可改）


def auto_config_path():
    return os.path.join(os.environ.get("LOCALAPPDATA", "."), "zmd-orb", "auto.json")


def load_auto():
    cfg = dict(AUTO_DEFAULTS)
    p = auto_config_path()
    if os.path.exists(p):
        try:
            with open(p, encoding="utf-8") as f:
                cfg.update({k: v for k, v in json.load(f).items() if k in AUTO_DEFAULTS})
        except Exception as e:
            log("[清理端] 读自动整理配置失败：%s" % e)
    return cfg


def save_auto(cfg):
    p = auto_config_path()
    try:
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception as e:
        log("[清理端] 写自动整理配置失败：%s" % e)


def auto_status():
    cfg = state.get("auto") or dict(AUTO_DEFAULTS)
    return {
        "enabled": bool(cfg.get("enabled")),
        "threshold_mb": cfg.get("threshold_mb"),
        "check_secs": cfg.get("check_secs"),
        "min_gap_secs": cfg.get("min_gap_secs"),
        "app_limit": cfg.get("app_limit", PROC_LIMIT),
        "count": state.get("auto_count", 0),
        "last": state.get("auto_last") or None,
        "reason": state.get("auto_reason", ""),
    }


def set_auto(q):
    """POST /auto?on=1&threshold_mb=2048&check_secs=60&min_gap_secs=180&app_limit=40"""
    cfg = state["auto"]
    if "on" in q:
        cfg["enabled"] = q["on"][0].lower() in ("1", "true", "yes")
    for key in ("threshold_mb", "check_secs", "min_gap_secs", "app_limit"):
        if key in q:
            try:
                cfg[key] = max(1, int(float(q[key][0])))
            except ValueError:
                pass
    save_auto(cfg)
    log("[清理端] 设置：自动整理 %s（阈值 %d MB，每 %d 秒看一次，间隔 ≥%d 秒），应用概况 %d 条"
        % ("开" if cfg["enabled"] else "关", cfg["threshold_mb"], cfg["check_secs"],
           cfg["min_gap_secs"], cfg["app_limit"]))
    return auto_status()


def auto_tick():
    """每 check_secs 看一次：空闲+零页低于阈值就做一次轻度整理。

    只做 l1：l2/l3 要管理员，自动流程里弹 UAC 不可接受；而且这条路上没有别的选择
    （清待命列表必须提权）。诚实说，l1 不会让 free+zero 变多——它只是把活跃工作集
    转成随时可回收的待命页，代价是那些页下次访问要硬缺页读回。所以默认关闭。
    """
    cfg = state.get("auto") or {}
    if not cfg.get("enabled"):
        return
    now = time.time()
    if now - state["auto_checked"] < cfg.get("check_secs", 60):
        return
    state["auto_checked"] = now

    fz = (read_memory_lists() or {}).get("free_zero_mb")
    if fz is None:
        state["auto_reason"] = "读不到空闲+零页，跳过"
        return
    if fz >= cfg["threshold_mb"]:
        state["auto_reason"] = "空闲+零页 %.0f MB ≥ 阈值 %d MB，不动" % (fz, cfg["threshold_mb"])
        return
    if now - state["auto_last"] < cfg.get("min_gap_secs", 180):
        state["auto_reason"] = ("空闲+零页 %.0f MB 偏低，但距上次自动整理不足 %d 秒"
                                % (fz, cfg["min_gap_secs"]))
        return
    if state["cleaning"]:
        state["auto_reason"] = "空闲+零页 %.0f MB 偏低，但正有一次整理在跑" % fz
        return

    res = do_clean("l1")          # 不带 force：与手动整理共用 30 秒冷却，避免连击
    if res.get("ok"):
        state["auto_last"] = time.time()
        state["auto_count"] += 1
        state["auto_reason"] = "空闲+零页 %.0f MB < 阈值 %d MB，已自动轻度整理" % (fz, cfg["threshold_mb"])
    else:
        state["auto_reason"] = "想自动整理但没成：%s" % (res.get("error") or "未知")


# ---------------- 设备数据（照搬 zmd-manager 的采集口径） ----------------
# 面板的"综合占用 / 设备性能"两页要这些东西：静态硬件只在启动取一次，显卡与硬盘性能计数器走慢循环。
SLOW_INTERVAL = 1.5     # 慢速采样（显卡 / 硬盘性能计数器）
PROC_LIMIT = 40         # 应用概况列表最多条目（与参考一致）
SUB_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
MEM_TYPE = {20: "DDR", 21: "DDR2", 24: "DDR3", 26: "DDR4", 34: "DDR5"}
_meta = {}              # pid -> {desc, title} 应用名缓存（照搬参考的缓存策略）


def _ps(cmd, timeout=8):
    """跑一段 PowerShell。强制 UTF-8 输出，免得中文在 cp936/cp1252 下乱掉。"""
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
             "$OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" + cmd],
            capture_output=True, text=True, encoding="utf-8", errors="ignore", timeout=timeout,
            creationflags=SUB_NO_WINDOW)
        return (r.stdout or "").strip()
    except Exception:
        return ""


def _ps_json(cmd, timeout=8):
    out = _ps("(%s) | ConvertTo-Json -Depth 5 -Compress" % cmd, timeout)
    if not out:
        return None
    try:
        return json.loads(out)
    except Exception:
        return None


def as_list(j):
    if j is None:
        return []
    return j if isinstance(j, list) else [j]


def collect_static():
    """CPU / 内存条 / 显卡 / 网卡 / 硬盘映射 —— 只在启动时取一次。"""
    s = {}
    cpu = as_list(_ps_json("Get-CimInstance Win32_Processor | "
                           "Select-Object Name,MaxClockSpeed,NumberOfLogicalProcessors"))
    cpu = cpu[0] if cpu else {}
    s["cpu_name"] = (cpu.get("Name") or platform.processor() or "处理器").strip()
    s["cpu_threads"] = cpu.get("NumberOfLogicalProcessors") or psutil.cpu_count(logical=True) or 1
    s["cpu_max"] = round((cpu.get("MaxClockSpeed") or 0) / 1000.0, 2)
    try:
        f = psutil.cpu_freq()
        s["cpu_base"] = round((f.min or f.current or 0) / 1000.0, 2) or s["cpu_max"]
    except Exception:
        s["cpu_base"] = s["cpu_max"]

    mems = as_list(_ps_json("Get-CimInstance Win32_PhysicalMemory | Select-Object Speed,SMBIOSMemoryType"))
    speeds = [m.get("Speed") for m in mems if m.get("Speed")]
    s["mem_speed"] = "%d MT/s" % max(speeds) if speeds else "—"
    mt = next((m.get("SMBIOSMemoryType") for m in mems if m.get("SMBIOSMemoryType")), None)
    s["mem_type"] = MEM_TYPE.get(mt, "DDR4" if mt is None else "物理内存")

    # 显卡取显存最大的那个（通常是独显）
    gpus = as_list(_ps_json("Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM"))
    best, best_ram = None, -1
    for g in gpus:
        ram = g.get("AdapterRAM") or 0
        if isinstance(ram, (int, float)) and ram > best_ram:
            best, best_ram = g, ram
    s["gpu_name"] = (best or {}).get("Name") or "显卡"
    s["gpu_mem_total"] = round(best_ram / (1024 ** 3), 1) if best_ram > 0 else 0.0

    nets = as_list(_ps_json("Get-CimInstance Win32_NetworkAdapter | "
                            "Where-Object {$_.NetEnabled -eq $true} | "
                            "Select-Object NetConnectionID,Speed"))
    n = nets[0] if nets else {}
    s["net_name"] = (n.get("NetConnectionID") or "网络").strip() or "网络"
    s["net_link"] = round((n.get("Speed") or 0) / 1e6) or 1000

    # 逻辑盘 -> 物理盘 映射 + 介质类型
    mapping = {}
    out = _ps("""
$d = Get-CimInstance Win32_DiskDrive
foreach ($x in $d) {
  $parts = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($x.DeviceID)'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"
  foreach ($p in $parts) {
    $logs = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"
    foreach ($l in $logs) { "$($l.DeviceID)|PhysicalDrive$($x.Index)|$($x.Model)" }
  }
}
""", timeout=12)
    for line in out.splitlines():
        seg = [x.strip() for x in line.split("|")]
        if len(seg) >= 2:
            mapping[seg[0]] = seg[1:]

    phys = as_list(_ps_json("Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType", 12))
    disks = []
    for p in psutil.disk_partitions():
        if "cdrom" in (p.opts or "") or not p.fstype:
            continue
        letter = p.device.rstrip("\\")
        m = mapping.get(letter, [])
        phys_name = m[0] if len(m) > 0 else ""
        model = m[1] if len(m) > 1 else ""
        media = ""
        for ph in phys:
            fn = ph.get("FriendlyName") or ""
            if fn and (fn in model or model in fn):
                mt2 = ph.get("MediaType") or ""
                bt = ph.get("BusType") or ""
                media = "NVMe SSD" if (mt2 == "SSD" and bt == "NVMe") else \
                        ("SATA SSD" if mt2 == "SSD" else (mt2 or "HDD"))
                break
        if media in ("", "Unspecified", None):
            media = "USB 存储" if "USB" in model.upper() else "本地磁盘"
        disks.append({"letter": letter, "mount": p.mountpoint, "phys": phys_name,
                      "model": model, "media": media})
    s["disks"] = disks
    return s


def sample_gpu():
    """显卡占用：先试 nvidia-smi（还能校正 Win32 里报错的显存），否则退到 GPU 引擎性能计数器。"""
    out = _ps("& nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total,clocks.sm "
              "--format=csv,noheader,nounits", timeout=4)
    if out and "," in out:
        seg = [x.strip() for x in out.splitlines()[0].split(",")]
        try:
            if len(seg) > 2 and float(seg[2]) > 0:
                state["stat"]["gpu_mem_total"] = round(float(seg[2]) / 1024.0, 1)
            return {"util": float(seg[0]), "mem_used": round(float(seg[1]) / 1024.0, 1),
                    "freq": float(seg[3]) if len(seg) > 3 else None, "ok": True}
        except Exception:
            pass
    val = _ps("$c = Get-Counter '\\GPU Engine(*engtype_3D)\\Utilization Percentage' "
              "-ErrorAction SilentlyContinue; "
              "if ($c) { ($c.CounterSamples | Measure-Object -Property CookedValue -Sum).Sum } else { 0 }",
              timeout=6)
    try:
        u = min(100.0, float(val))
    except Exception:
        u = 0.0
    return {"util": u, "mem_used": None, "freq": None, "ok": False}


def sample_disk_io(letters):
    """优先 WMI 性能类（不受系统语言影响），失败再退回 Get-Counter。"""
    res = {}
    j = _ps_json("Get-CimInstance Win32_PerfFormattedData_PerfDisk_LogicalDisk | "
                 "Select-Object Name,PercentDiskTime,DiskReadBytesPersec,DiskWriteBytesPersec",
                 timeout=8)
    for s in as_list(j):
        if not isinstance(s, dict):
            continue
        name = (s.get("Name") or "").strip()
        if len(name) == 2 and name.endswith(":"):
            res[name] = {"% Disk Time": s.get("PercentDiskTime") or 0,
                         "Disk Read Bytes/sec": s.get("DiskReadBytesPersec") or 0,
                         "Disk Write Bytes/sec": s.get("DiskWriteBytesPersec") or 0}
    if res or not letters:
        return res

    paths = []
    for L in letters:
        paths += ["'\\LogicalDisk(%s)\\%% Disk Time'" % L,
                  "'\\LogicalDisk(%s)\\Disk Read Bytes/sec'" % L,
                  "'\\LogicalDisk(%s)\\Disk Write Bytes/sec'" % L]
    cmd = ("$c = Get-Counter -Counter @(%s) -ErrorAction SilentlyContinue; "
           "if ($c) { $c.CounterSamples | Select-Object Path,CookedValue }" % ",".join(paths))
    for s in as_list(_ps_json(cmd, timeout=8)):
        if not isinstance(s, dict):
            continue
        m = re.search(r"\\LogicalDisk\(([^)]+)\)\\(.+)$", s.get("Path", ""))
        if m:
            res.setdefault(m.group(1), {})[m.group(2).strip()] = s.get("CookedValue") or 0
    return res


def slow_loop():
    """显卡与硬盘性能计数器走 1.5 秒的慢循环（照搬参考的节奏）。"""
    while True:
        try:
            if state.get("stat"):
                state["gpu"] = sample_gpu()
                state["disk_io"] = sample_disk_io(state.get("disk_letters", []))
        except Exception:
            pass
        time.sleep(SLOW_INTERVAL)


def enrich(procs):
    """进程显示名：exe 的描述优先，回落进程名；顺带取主窗口标题（缓存，只查新 pid）。"""
    need = [p["pid"] for p in procs if p["pid"] not in _meta]
    if need:
        for i in range(0, len(need), 25):
            ids = ",".join(str(x) for x in need[i:i + 25])
            j = _ps_json("Get-Process -Id %s -ErrorAction SilentlyContinue | "
                         "Select-Object Id,Description,MainWindowTitle" % ids, timeout=6)
            for m in as_list(j):
                if isinstance(m, dict) and m.get("Id") is not None:
                    _meta[int(m["Id"])] = {"desc": m.get("Description") or "",
                                           "title": m.get("MainWindowTitle") or ""}
        for pid in need:
            _meta.setdefault(pid, {"desc": "", "title": ""})
    for p in procs:
        m = _meta.get(p["pid"], {})
        p["display"] = (m.get("desc") or p["name"].replace(".exe", "") or "未知").strip()
        p["title"] = m.get("title") or ""


# ---------------- HTTP ----------------
STATIC = {"ball.html", "panel.html", "dev.html", "ring.js", "api.js"}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        # 不给 Access-Control-Allow-Origin：本工具没有跨源的前端（dev.html 也是采集端自己伺服的），
        # 敞开 CORS 只会让任意网页能读取本机 API 的返回。
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    SHELL_TOKEN = "zmd-orb-shell"

    def _need_token(self, what):
        """写操作（整理/结束进程/开关）要求带 X-Zmd-Orb 头。

        这挡的是"网页里的脚本偷偷 POST 到本机 API"：带自定义头的跨源请求会先发预检，
        而采集端不答预检，浏览器就拦下了。本机的原生程序（壳、curl）照旧能调。
        """
        if self.headers.get("X-Zmd-Orb") == self.SHELL_TOKEN:
            return True
        self._send(403, json.dumps({"ok": False, "error": "缺少 X-Zmd-Orb 头（%s 属写操作）" % what},
                                   ensure_ascii=False))
        return False

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path, q = parsed.path, urllib.parse.parse_qs(parsed.query)
        if path == "/health":
            self._send(200, json.dumps({"service": "zmd-orb-collector", "port": PORT,
                                        "ready": state.get("snapshot") is not None,
                                        "admin": is_admin()}))
            return
        if path.startswith("/snapshot"):
            snap = state.get("snapshot")
            if snap is None:
                self._send(503, json.dumps({"live": False, "error": "采样尚未就绪"}))
            else:
                self._send(200, json.dumps(snap, ensure_ascii=False))
            return
        if path.rstrip("/") == "/processes":
            rows = state.get("procs")
            if not rows or time.time() - (state.get("procs_ts") or 0) > 2.5:
                rows = sample_procs()          # 缓存太旧（采样线程刚起）就现采一次
            self._send(200, json.dumps({"ts": time.time(), "window_s": state.get("procs_dt"),
                                        "count": len(rows), "procs": rows}, ensure_ascii=False))
            return
        if path.rstrip("/") == "/auto":
            self._send(200, json.dumps(auto_status(), ensure_ascii=False))
            return
        if path.rstrip("/").endswith("/clean/result"):
            self._send(200, json.dumps(state.get("last_result") or
                                       {"ok": None, "error": "还没整理过"}, ensure_ascii=False))
            return
        if path.rstrip("/") == "/clean":
            if self._need_token("整理"):     # GET 也认，方便 curl 手工测
                self._clean(q)
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
                                    "endpoints": ["/health", "/snapshot", "/clean?tier=l1|l2|l3",
                                                  "/clean/result", "/auto"]}, ensure_ascii=False))

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(parsed.query)
        ln = int(self.headers.get("Content-Length") or 0)
        if ln:                      # 表单体也认（壳直接用 query，这里留给手工测）
            try:
                body = self.rfile.read(ln).decode("utf-8", "replace")
                for k, v in urllib.parse.parse_qs(body).items():
                    q.setdefault(k, v)
            except Exception:
                pass
        if parsed.path.rstrip("/") == "/clean":
            if self._need_token("整理"):
                self._clean(q)
            return
        if parsed.path.rstrip("/") == "/kill":
            if self._need_token("结束进程"):
                self._kill(q)
            return
        if parsed.path.rstrip("/") == "/auto":
            if self._need_token("开关自动整理"):
                self._send(200, json.dumps(set_auto(q), ensure_ascii=False))
            return
        self._send(404, json.dumps({"error": "未知路径 %s" % parsed.path}, ensure_ascii=False))

    def _kill(self, q):
        try:
            pid = int((q.get("pid") or ["0"])[0])
        except ValueError:
            self._send(400, json.dumps({"ok": False, "error": "pid 必须是整数"}, ensure_ascii=False))
            return
        tree = (q.get("tree") or ["0"])[0].lower() in ("1", "true", "yes")
        res = kill_process(pid, tree)      # 可能弹一次 UAC（权限不足时按需提权）
        self._send(200 if res.get("ok") else 409, json.dumps(res, ensure_ascii=False))

    def _clean(self, q):
        tier = (q.get("tier") or ["l1"])[0]
        force = (q.get("force") or ["0"])[0].lower() in ("1", "true", "yes")
        # l2/l3 可能要弹 UAC，用户可能在对话框上停留，所以这里会阻塞一会儿（壳要放宽超时）
        res = do_clean(tier, force)
        self._send(200 if res.get("ok") else 409, json.dumps(res, ensure_ascii=False))

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
        msg = "live=%s mem=%.1f/%.1fG %.1f%% commit=%.1f/%.1fG cpu=%.1f%% 硬缺页=%.0f/s 自动整理=%s" % (
            data.get("live"), (mem.get("used_mb", 0)) / 1024, (mem.get("total_mb", 0)) / 1024,
            mem.get("pct", 0), mem.get("committed_mb", 0) / 1024, mem.get("commit_limit_mb", 0) / 1024,
            (data.get("cpu") or {}).get("util", 0),
            data.get("hard_fault_rate") or 0,
            "开" if (data.get("auto") or {}).get("enabled") else "关")
    try:
        with open(os.path.join(os.environ.get("TEMP", "."), "zmd_orb_selftest.txt"),
                  "w", encoding="utf-8") as f:
            f.write("%s\n%s\n" % ("PASS" if ok else "FAIL", msg))
    except Exception:
        pass
    log("[采集端] selftest %s %s" % ("PASS" if ok else "FAIL", msg))


def main(gui=True, selftest=False, clean_now=None, out_path=None, kill_now=None, kill_tree=False):
    global _fault
    use_utf8_stdio()

    if clean_now or kill_now:
        # 提权 helper 模式：做完一件事、把结果写进文件就退出（不占端口、不常驻）
        try:
            if clean_now:
                res = do_clean(clean_now, force=True)
            else:
                res = kill_process(int(kill_now), tree=kill_tree)
        except Exception as e:
            import traceback
            res = {"ok": False, "error": "helper 崩了：%s" % e, "trace": traceback.format_exc()}
            log("[清理端] helper 异常：\n%s" % res["trace"])
        if out_path:
            try:
                with open(out_path, "w", encoding="utf-8") as f:
                    json.dump(res, f, ensure_ascii=False)
            except Exception as e:
                log("[清理端] 写结果失败：%s" % e)
                return 1
        else:
            print(json.dumps(res, ensure_ascii=False, indent=2))
        return 0 if res.get("ok") else 1

    mem = read_memory() or {}
    state["phys_bytes"] = int((mem.get("total_mb") or 0) * MB)   # 读工作集时的回绕兜底用
    log("[采集端] 内存 %.1f GB，当前占用 %.1f%%（提交 %.1f/%.1f GB）"
        % ((mem.get("total_mb", 0)) / 1024, mem.get("pct", 0),
           (mem.get("committed_mb", 0)) / 1024, (mem.get("commit_limit_mb", 0)) / 1024))
    log("[采集端] 空闲+零页 %.1f GB，待命列表 %.1f GB，已修改 %.1f GB，管理员=%s"
        % ((mem.get("free_zero_mb", 0)) / 1024, (mem.get("standby_mb", 0)) / 1024,
           (mem.get("modified_mb", 0)) / 1024, is_admin()))
    state["auto"] = load_auto()
    _fault = PdhRate()
    log("[采集端] 硬缺页率计数器：%s" % ("就绪（\\Memory\\Pages Input/sec）" if _fault.q else _fault.error))
    log("[采集端] 自动整理：%s，阈值 %d MB，每 %d 秒看一次，间隔 ≥%d 秒"
        % ("开" if state["auto"]["enabled"] else "关", state["auto"]["threshold_mb"],
           state["auto"]["check_secs"], state["auto"]["min_gap_secs"]))

    # 设备页要的静态硬件信息（照搬 zmd-manager：PowerShell 取一次，几秒钟）
    log("[采集端] 读取静态硬件信息 …")
    try:
        state["stat"] = collect_static()
    except Exception as e:
        log("[采集端] 静态硬件信息失败：%s" % e)
        state["stat"] = {}
    state["disks_static"] = state["stat"].get("disks", [])
    state["disk_letters"] = [d["letter"] for d in state["disks_static"]]
    log("[采集端] CPU: %s（%s 线程，基准 %s GHz / 最大 %s GHz）"
        % (state["stat"].get("cpu_name"), state["stat"].get("cpu_threads"),
           state["stat"].get("cpu_base"), state["stat"].get("cpu_max")))
    log("[采集端] 显卡: %s；内存: %s %s；硬盘: %s"
        % (state["stat"].get("gpu_name"), state["stat"].get("mem_type"),
           state["stat"].get("mem_speed"),
           ", ".join("%s(%s)" % (d["letter"], d["media"]) for d in state["disks_static"])))

    threading.Thread(target=slow_loop, daemon=True).start()
    threading.Thread(target=sampler, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    log("[采集端] 已启动： http://127.0.0.1:%d/ （Ctrl+C 退出）" % PORT)

    if selftest:
        time.sleep(2.5)
        _selftest()
        srv.shutdown()
        return 0

    if gui:
        log("[采集端] 打开浏览器页面： http://127.0.0.1:%d/ball.html" % PORT)
        webbrowser.open("http://127.0.0.1:%d/dev.html" % PORT)

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        log("[采集端] 已退出")
        srv.shutdown()
    return 0


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="终末地加速球 - 本地采集端/清理端")
    ap.add_argument("--no-gui", action="store_true", help="仅启动服务，不打开界面")
    ap.add_argument("--selftest", action="store_true", help="启动后自测一次并退出")
    ap.add_argument("--clean-now", metavar="TIER", choices=("l1", "l2", "l3"),
                    help="立刻整理一次就退出（提权 helper 用，不占端口）")
    ap.add_argument("--kill-now", metavar="PID", help="立刻结束这个进程就退出（提权 helper 用）")
    ap.add_argument("--tree", action="store_true", help="配合 --kill-now：连子孙进程一起结束")
    ap.add_argument("--out", metavar="FILE", help="把 --clean-now / --kill-now 的结果写成 JSON 到这个文件")
    a = ap.parse_args()
    sys.exit(main(gui=not a.no_gui, selftest=a.selftest, clean_now=a.clean_now, out_path=a.out,
                  kill_now=a.kill_now, kill_tree=a.tree))