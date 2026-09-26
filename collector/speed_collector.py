# -*- coding: utf-8 -*-
"""终末地加速球 —— 本地采集端 / 清理端

依赖：pip install psutil
运行：python collector/speed_collector.py [--no-gui | --selftest]
对外：http://127.0.0.1:8910/snapshot      内存/CPU 快照（JSON，带 CORS）
      http://127.0.0.1:8910/health        健康检查（壳启动前探活用：是我们的采集端就直接复用）
      http://127.0.0.1:8910/clean?tier=   三级整理缓存页（l1 免提权 / l2、l3 需管理员，按需提权）
      http://127.0.0.1:8910/clean/result  最近一次整理的结果
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
"""
import ctypes
import ctypes.wintypes as wintypes
import json
import os
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

state = {"snapshot": None, "last_result": None, "last_clean": 0.0, "cleaning": False}
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
                    "admin": is_admin(),      # 壳/面板据此提示"深度整理需要管理员"
                    "mem": mem,
                    "cpu": read_cpu(),
                    "boot": round(time.time() - psutil.boot_time()),
                }
        except Exception:
            pass
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


def enumerate_processes():
    """[(pid, 小写进程名)]。失败返回空列表，调用方回退 psutil。"""
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
        out = []
        off = 0
        while True:
            p = ctypes.cast(ctypes.byref(buf, off),
                            ctypes.POINTER(SYSTEM_PROCESS_INFORMATION)).contents
            pid = int(p.UniqueProcessId or 0)
            if pid:
                name = ""
                if p.ImageName.Buffer and p.ImageName.Length:
                    name = ctypes.wstring_at(p.ImageName.Buffer, p.ImageName.Length // 2).lower()
                out.append((pid, name))
            if not p.NextEntryOffset:
                break
            off += p.NextEntryOffset
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
                procs.append((p.info["pid"], (p.info.get("name") or "").lower()))
            except Exception:
                pass
    done = skipped = failed = 0
    for pid, name in procs:
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
    return read_memory() or {}


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
    """按需提权：用 runas 把采集端自己再拉一份，做完把结果写进临时文件再退出。"""
    out = os.path.join(tempfile.gettempdir(), "zmd_orb_clean_%d.json" % os.getpid())
    try:
        if os.path.exists(out):
            os.remove(out)
    except OSError:
        pass

    if getattr(sys, "frozen", False):
        exe, params = sys.executable, '--clean-now %s --out "%s"' % (tier, out)
    else:
        exe = sys.executable
        params = '"%s" --clean-now %s --out "%s"' % (os.path.abspath(__file__), tier, out)
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
        log("[清理端] %s 未执行：%s" % (tier, msg))
        res = {"ok": False, "tier": tier, "error": msg, "need_admin": True, "ts": time.time()}
        state["last_result"] = res
        return res

    proc = sei.hProcess
    try:
        deadline = time.time() + ELEVATE_WAIT
        while time.time() < deadline:
            if os.path.exists(out):
                return _read_helper_result(tier, out)
            # 每次等 300ms：既能及时发现进程退出，也不至于空转
            if proc and _k32.WaitForSingleObject(proc, 300) == WAIT_OBJECT_0:
                if os.path.exists(out):
                    return _read_helper_result(tier, out)
                code = ctypes.c_ulong(0)
                _k32.GetExitCodeProcess(proc, ctypes.byref(code))
                msg = ("提权 helper 直接退出了（退出码 %d）且没写出结果；"
                       "多半是命令行参数有问题，看壳日志里那行'提权拉起'" % code.value)
                log("[清理端] %s 失败：%s" % (tier, msg))
                res = {"ok": False, "tier": tier, "error": msg, "need_admin": True,
                       "ts": time.time()}
                state["last_result"] = res
                return res
    finally:
        if proc:
            _k32.CloseHandle(proc)

    res = {"ok": False, "tier": tier, "need_admin": True,
           "error": "提权后的整理超时（等了 %d 秒）" % ELEVATE_WAIT}
    log("[清理端] %s 失败：%s" % (tier, res["error"]))
    state["last_result"] = res
    return res


def _read_helper_result(tier, out):
    try:
        with open(out, encoding="utf-8") as f:
            res = json.load(f)
    except Exception as e:
        res = {"ok": False, "tier": tier, "error": "读不到提权结果：%s" % e}
    try:
        os.remove(out)
    except OSError:
        pass
    if res.get("ok"):
        state["last_clean"] = time.time()
    state["last_result"] = res
    log("[清理端] 提权完成 %s" % (res.get("summary") or res.get("error", "")))
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
                       "system_cache_mb", "committed_mb", "avail_mb")},
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
        if path.rstrip("/").endswith("/clean/result"):
            self._send(200, json.dumps(state.get("last_result") or
                                       {"ok": None, "error": "还没整理过"}, ensure_ascii=False))
            return
        if path.rstrip("/") == "/clean":
            self._clean(q)          # GET 也认，方便 curl 手工测
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
                                                  "/clean/result"]}, ensure_ascii=False))

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
            self._clean(q)
            return
        self._send(404, json.dumps({"error": "未知路径 %s" % parsed.path}, ensure_ascii=False))

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


def main(gui=True, selftest=False, clean_now=None, out_path=None):
    use_utf8_stdio()

    if clean_now:
        # 提权 helper 模式：只整理一次，把结果写进文件就退出（不占端口、不常驻）
        try:
            res = do_clean(clean_now, force=True)
        except Exception as e:
            import traceback
            res = {"ok": False, "tier": clean_now, "error": "helper 崩了：%s" % e,
                   "trace": traceback.format_exc()}
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
    log("[采集端] 内存 %.1f GB，当前占用 %.1f%%（提交 %.1f/%.1f GB）"
        % ((mem.get("total_mb", 0)) / 1024, mem.get("pct", 0),
           (mem.get("committed_mb", 0)) / 1024, (mem.get("commit_limit_mb", 0)) / 1024))
    log("[采集端] 空闲+零页 %.1f GB，待命列表 %.1f GB，已修改 %.1f GB，管理员=%s"
        % ((mem.get("free_zero_mb", 0)) / 1024, (mem.get("standby_mb", 0)) / 1024,
           (mem.get("modified_mb", 0)) / 1024, is_admin()))
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
    ap.add_argument("--out", metavar="FILE", help="把 --clean-now 的结果写成 JSON 到这个文件")
    a = ap.parse_args()
    sys.exit(main(gui=not a.no_gui, selftest=a.selftest, clean_now=a.clean_now, out_path=a.out))