# 终末地加速球 · zmd-orb

常驻悬浮球：一键**整理内存缓存页**，同时能当任务管理器用。环的样式沿用 `zmd-manager`（终末地管理器）的电量环。

> **状态：M3 完成。** 已有：双窗口壳（透明置顶球 + 面板）、采集端 `/snapshot`、球的电量环与粒子团、
> **三级整理真生效**（按需 UAC、前后测量、节流、排除名单）、**硬缺页率**与**自动整理**（默认关）、
> **面板进程表**（排序/搜索/结束进程，可连子孙）。托盘与开机自启在 M4。

## 它做什么、不做什么

Windows 上没有"释放内存"的魔法。所谓加速球做的是两件事：把冷进程的工作集换出去、把待命列表（文件缓存）清空。
被换出的页在进程下次访问时会**硬缺页**从 pagefile 读回来——所以它不产生内存，只是把内存从"占用"搬到"可用"。

因此本工具的显示口径刻意保守：

- 文案用"**整理 X GB 缓存页**"或"**换出 X GB 工作集**"，不写"释放内存"；轻度档还会明说"free+zero 没变"
- 面板同时给出 **空闲 + 零页（free+zero）** 与 **待命列表**，而不是只看 `available`
  ——`available` 把待命列表算作可用，清空它以后 `available` 几乎不动，而 free+zero 会上去
- 真会打崩程序的是**提交额度**（`commit_limit` 与本机页面文件大小绑定），所以它单独占一张卡

### 三级整理（档位单调：深度包含轻度）

| 档 | 做什么 | 提权 | 效果（本机实测） |
|---|---|---|---|
| **l1 轻度** | 清本用户进程的工作集 | 免提权 | 198 个进程 → 换出 1~5 GB 工作集（进待命/已修改列表），占用率 63%→53%，**free+zero 不变** |
| **l2 深度** | l1 + 刷已修改页 + **清待命列表** | 需管理员（弹 UAC） | 待命列表能掉几 GB（本机常态 8~13 GB），free+zero 相应上升 |
| **l3 全部** | l2 的全系统版：全系统清工作集 + 清系统文件缓存 + 清低优先级待命 | 需管理员（弹 UAC） | 最狠，硬缺页代价也最大 |

单击球 = **l1**（免提权、不弹 UAC，1.3 秒内完成）；两个重档在面板上，点了才弹一次 UAC。

实测的特权边界（2026-09-26 逐条核过，避免"以为要管理员"或"以为不要"）：

- **免提权就能成功**：`K32EmptyWorkingSet`（本用户 214 个进程里能打开 194 个）、
  `NtSetSystemInformation(0x50, 3)` 刷已修改页
- **必须管理员**：`(0x50, 4/5)` 清待命、`(0x50, 2)` 全系统清工作集、`SetSystemFileCacheSize`
  （无提权时前者返回 `0xC0000061 STATUS_PRIVILEGE_NOT_HELD`，后者返回 0 且 `GetLastError()=5`）

排除名单：默认不动球自己、采集端、关键系统进程，并且**不清当前前台进程**（正在用的程序被清工作集会有可见卡顿）。
想加就写 `%LOCALAPPDATA%\zmd-orb\exclude.txt`（或程序同目录 `exclude.txt`）：
每行一个进程名，`#` 注释，`!名字` 表示从默认名单里去掉，`foreground_exclude=0` 关掉前台保护。

### 硬缺页率：整理的代价

整理把页从工作集/缓存里搬走，代价是那些页下次被访问时要从 pagefile/磁盘**硬缺页**读回来
（`\Memory\Pages Input/sec`）。面板上有一张卡实时显示它，每次整理结果的 before/after/delta 里也带着。

取法是进程内 PDH，用 **`PdhAddEnglishCounterW`（英文计数器名）**：中文系统上计数器路径是本地化的，
`PdhAddCounter` 传英文名会找不到计数器。单次采样 0.14~0.38 ms，比每次拉 PowerShell 快三个数量级。
它是速率型指标、波动很大，看趋势别看单点。

### 自动整理（默认关闭）

策略：每 60 秒看一次，**空闲 + 零页**低于 2048 MB 就做一次轻度整理，两次之间至少隔 180 秒。

- **只做 l1**：清待命列表必须管理员，自动流程里弹 UAC 不可接受
- **默认关闭**：l1 不会让 free+zero 变多（实测 −0.03 ~ −0.22 GB），只是把活跃工作集转成随时可回收的
  待命页，代价是那些页下次访问要硬缺页。这笔账划不划算该由你自己决定，工具不替你默认打开
- 参数落在 `%LOCALAPPDATA%\zmd-orb\auto.json`；也可以
  `POST /auto?on=1&threshold_mb=2048&check_secs=60&min_gap_secs=180`
- 每次检查的判断原因都记着（面板上显示"上次判断：…"），没有黑箱

### 进程表与结束进程（M3）

- 进程表来自**一次** `NtQuerySystemInformation(SystemProcessInformation)`（403 个进程约 17~32ms）：
  名字、内存、线程数、CPU 时间都在同一条记录里，不需要按进程逐个 `OpenProcess`
- **CPU% 是两次采样的差**再按逻辑核数归一 —— 所以**第一次打开全是 0**（Task Manager 也是这样），
  并且面板只在**可见时**每 2 秒采一次（收起就不采）。16 逻辑核上跑单线程满载，实测显示 6.3%（psutil 同刻 6.2%）
- 结束进程：单个或连子孙（`tree=1`，子孙先死）。**拦住**内核/关键进程（System/smss/csrss/wininit/services/
  lsass/winlogon/Registry/Memory Compression/fontdrvhost）与本工具自己的进程（父进程链，含壳）；
  权限不足（其他用户或受保护进程）时**按需提权**再试一次，和整理共用同一套 helper 机制
- 面板里受保护的进程会被压暗；点"结束进程"弹确认框（结束是不可逆的），结果写在状态行

## 为什么壳是 WPF（而不是 Web 壳）

第一版壳用 Tauri 2 + WebView2，撞上一个平台级缺陷：**窗口一旦移动（拖动、换显示器、切焦点），
WebView2 那层的合成表面就会丢 alpha**，实机表现是球泛出一层浅色底（黑底上是一圈淡蓝、白底上是灰盘）。
窗口级手段（`InvalidateRect`+`UpdateWindow`、`RedrawWindow` 带 `RDW_ALLCHILDREN`、无效化 WRY 子窗口、
尺寸抖 1px、Z 序翻转）实测都清不掉，只有 hide+show 重建显示状态才回到 0——于是只能靠
"拖动期间把球藏起来 + 松手后重建 + 轮询位置逐帧重绘"打补丁，交互与观感都是将就。

WPF 的 `AllowsTransparency` 走的是**分层窗口**（ARGB DIB + DWM 合成）：

- 移动窗口不触发内容重绘 → 从原理上不存在"移动后丢 alpha"
- 鼠标命中测试按**像素 alpha**：四角透明处自动点击穿透，不需要 `SetWindowRgn` 裁剪
- 于是 `begin_drag` / `end_drag_cleanup` / `refresh_ball_window` / 位置轮询那一整套补丁全部删除

代价：分层窗口是软件渲染，且要常驻 .NET 运行时（发布包自带，约 70 MB 压完）。

## 架构

| 层 | 内容 |
|---|---|
| 壳 | `orb/`：WPF / .NET 6。`BallWindow`（透明/置顶/不进任务栏的球）+ `PanelWindow`（任务管理器），环与粒子团是 `BallVisual` 里的 `DrawingContext` 直绘 |
| 内核 | `collector/speed_collector.py`：psutil + ctypes，本地 API `127.0.0.1:8910` |
| 设计参考 | `frontend/`：最初那套 HTML/SVG/Canvas 实现，**不再参与运行时**；`dev.html` 在浏览器里模拟透明底预览环的配色，调色时比反复编译省事 |

本地 API 端口用 **8910**，避开 zmd-manager 的 8899，两个工具可以同时开。

对外接口：

```
GET  /snapshot        内存/CPU 快照（free_zero_mb / standby_mb / modified_mb / hard_fault_rate / auto / admin）
GET  /health          健康检查（壳启动前探活用）
POST /clean?tier=l1   l2 / l3 整理一次；返回 summary/detail/before/after/delta/timing
GET  /clean/result    最近一次整理的结果（面板打开时回填）
POST /auto?on=1       开关自动整理（GET /auto 看状态与"上次判断"的原因）
GET  /processes       进程表（pid/名字/内存/CPU%/线程/是否受保护）
POST /kill?pid=N      结束进程（tree=1 连子孙）；权限不足时按需提权
GET  /dev.html        设计参考页（调色用）
```

写操作（`/clean`、`/kill`、`/auto`）要求带 `X-Zmd-Orb: zmd-orb-shell` 头，否则 403；
采集端也**不再回 `Access-Control-Allow-Origin`**。这两条一起挡的是"网页脚本偷偷 POST 到本机 API"
（带自定义头的跨源请求会先发预检，采集端不答预检，浏览器就拦下了；本机原生程序照旧能调）。

手工测（`force=1` 跳过 30 秒节流）：

```
curl -X POST "http://127.0.0.1:8910/clean?tier=l1&force=1"
python collector/speed_collector.py --clean-now l2      # 提权 helper 的入口，只整理一次不占端口
```

## 构建

本机就能编译运行，不用再过 CI（这是换掉 Rust/Tauri 的另一个好处）：

```
dotnet build orb/ZmdOrb.csproj -c Debug
orb\bin\Debug\net6.0-windows\zmd-orb.exe
```

需要 .NET 6 SDK（含 WindowsDesktop）。调试运行**不需要** backend.exe：
壳会沿目录树向上找 `collector/speed_collector.py`，用本机 python 跑（可用 `ZMD_ORB_PYTHON` 指定解释器）。

发布版是自包含的（目标机不需要装 .NET）：

```
python build_exe.py                 # → dist/backend.exe（需 pyinstaller、psutil）
copy dist\backend.exe orb\backend.exe
dotnet publish orb\ZmdOrb.csproj -c Release -r win-x64 --self-contained true
```

CI（`.github/workflows/build.yml`）把上面这套跑一遍，产出 `zmd-orb-win-x64.zip`；
打包前会**真启动一次发布产物**并等 `127.0.0.1:8910/health` 出数，确认壳能把 backend.exe 拉起来。
安装包（Inno/NSIS）留到 M4 和托盘、开机自启一起做。

## 开发约定

- **窗口行为与动画在 C#（壳），取数与清理在 Python（内核）。** 壳现在本机可编译可运行，
  改视觉直接 `dotnet build` 重开即可；不要为了"免得编译"把窗口行为挪出去。
- 壳只负责窗口与进程生命周期；内存口径、清理策略都在 `collector/` 里。
- 视觉参数（环半径/线宽/配色/粒子数/帧率）集中在 `orb/Ring.cs`，与 `frontend/ring.js` 一一对应。

## 目录

```
orb/                             WPF 壳（.NET 6）
  BallWindow.xaml(.cs)           球窗口：取数、单击跑轻度整理、拖拽/双击/右键、悬停
  BallVisual.cs                  电量环 + 中心粒子团（直绘，参数对应 ring.js）
  PanelWindow.xaml(.cs)          面板：三档整理 + 六张读数卡（M3 加进程表）
  Ring.cs                        环的几何与配色常量
  MemoryApi.cs / BackendProcess.cs / Win32.cs / Diag.cs
collector/speed_collector.py    本地采集/清理服务（psutil + ctypes）
frontend/                       设计参考（不再参与运行时）
build_exe.py                    打 backend.exe
make_icon.py                    生成 orb/icon.ico
.github/workflows/build.yml     CI：出自包含 zip + 冒烟测试
```

## 已知取巧与坑

- **`WS_EX_TOOLWINDOW` 要自己加**：WPF 的 `ShowInTaskbar=false` 实测只做到"不加 `WS_EX_APPWINDOW`"，
  球照样占任务栏按钮与 Alt-Tab，所以 `Win32.HideFromTaskbar` 手动改扩展样式（和当年 Tauri 是同一个坑）。
- **进度弧的辉光**：原 SVG 是 `drop-shadow(0 0 7px)`，WPF 用"三层递减宽度的半透明描边"近似，
  并裁剪掉主环内缘以内的部分（否则最外层描边会糊到内盘与主环之间那道透明缝上）。凑近看会比原来略硬。
- **采集端孤儿**：壳被强杀时 python 采集端会留下来占着 8910。壳启动前先探一次 `/health`，
  是我们的采集端就直接复用，不重复拉起。
- **面板窗口不透明**：面板不需要透明层，走硬件加速，文字更清晰。
- **别用 psutil 枚举进程**：`process_iter(["pid","name"])` 在本机要 **2114ms**（它每个进程都得
  `OpenProcess` 取名字），比"清工作集"本身（175ms）还慢十倍，于是单击一次要 3.3 秒。
  改用 `NtQuerySystemInformation(SystemProcessInformation)` 一次拿全 404 个进程只要 **16.7ms**（快 127 倍），
  单击总耗时降到 1.3 秒。psutil 仍留着做 CPU/启动时间等核对，枚举失败时才回退它。
- **`SYSTEM_MEMORY_LIST_INFORMATION` 要比 phnt 长**：本机实测 OS 要 **176 字节**（22 个 ULONG_PTR），
  按 phnt 的 15 字段（120 字节）申请会拿到 `0xC0000004 STATUS_INFO_LENGTH_MISMATCH`，字段一个都不填。
  前 13 个字段与 WMI 的 `Win32_PerfFormattedData_PerfOS_Memory` 逐条对得上（待命合计差 <2MB），
  尾部 7 个字段含义不明且按页换算会超过物理内存，一律不用。
- **`FlushModifiedList` 免提权就能成功**（实测 `0x00000000`），而清待命/全系统清工作集/清文件缓存都要管理员
  ——别以为"系统级操作必然都要提权"。
- **`SYSTEM_PROCESS_INFORMATION` 的字段偏移是"对出来"的，不是照头文件抄的**：这个结构在
  `InheritedFromUniqueProcessId` 之后有多个版本（Winternl.h 用 `SIZE_T`、phnt 用 `ULONG`）。
  实现里用显式偏移读，并且每个偏移都拿 psutil 对过账：CPU 时间（40/48，100ns）与 psutil 逐位吻合，
  工作集（144，字节）跨量级核对 0 误差（最大 2.5GB 的 memory compression 也对得上）。别改成"照抄结构体"。