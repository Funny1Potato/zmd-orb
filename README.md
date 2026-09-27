# 终末地加速球 · zmd-orb

常驻悬浮球：一键**整理内存缓存页**，同时能当任务管理器用。环的样式沿用 `zmd-manager`（终末地管理器）的电量环。

> **状态：M3 完成。** 已有：双窗口壳（透明置顶球 + 面板）、采集端 `/snapshot`、球的电量环与粒子团、
> **三级整理真生效**（按需 UAC、前后测量、节流、排除名单）、**硬缺页率**与**自动整理**（默认关）、
> **系统信息页**（机器/软件环境规格网格）。第 4 页原来的**进程表**已按反馈下线（改成了系统信息）；
> 采集端的 `/processes` 与 `/kill` 仍在，可用命令行或 HTTP 直接调用。托盘与开机自启在 M4。

## 它做什么、不做什么

Windows 上没有"释放内存"的魔法。所谓加速球做的是两件事：把冷进程的工作集换出去、把待命列表（文件缓存）清空。
被换出的页在进程下次访问时会**硬缺页**从 pagefile 读回来——所以它不产生内存，只是把内存从"占用"搬到"可用"。

因此本工具的显示口径刻意保守：

- 文案用"**整理 X GB 缓存页**"或"**换出 X GB 工作集**"，不写"释放内存"；轻度档还会明说"free+zero 没变"
- 球面只显示**占用率一个数**（原来是"占用率 + 一行小字：可用 xx G · 提交 xx%"，那行看不清，删了）。
  提交额度 ≥85% 时这个数换成**橙色**——真会把程序打崩的是提交额度，不再靠小字说明
- 整理结果在球上用一行**带深色底的浮字**给出（"换出 X G 工作集" / "整理 X G 缓存页"，1.7 秒后淡出），
  口径说明与整理明细留在**面板与日志**里；回落演示数据时球上的数字会调暗
- 面板同时给出 **空闲 + 零页（free+zero）** 与 **待命列表**，而不是只看 `available`
  ——`available` 把待命列表算作可用，清空它以后 `available` 几乎不动，而 free+zero 会上去
- 真会打崩程序的是**提交额度**（`commit_limit` 与本机页面文件大小绑定），所以它单独占一张卡
- 首页中间的**综合占用**是两道换算：`综合% =（权重CPU×CPU占用率 + 权重内存×内存占用率）÷ 两权重之和`，
  再按**分母**折成 MB 显示（`综合% ÷ 100 × 分母`）。分母默认 **325799 MB**、权重默认 **0.4 / 0.6**，
  都在"显示设置"里改（分母填 0 = 跟随提交额度上限；权重只影响两者的相对比例，0.4/0.6 与 4/6 等价）
- 首页两个方框给的是**绝对值**（单位 GB，一位小数）：右上**已占用内存**（物理内存口径）、左下**已提交**（提交额度口径）
- 应用列表（首页"应用概况"与"应用内存"页）**不含"已压缩内存"**（`Memory Compression`，老版本叫 `MemCompression`）：
  它不是一个应用，工作集就是压缩池本身（本机常态 2 GB 上下），单列出来等于把内存占用算重；
  任务管理器里它也只是内存页上的"已压缩"数字。进程表（第 4 页）是进程口径，仍然列它
- **"已占用"与"已提交"不是同一个口径**：工作集是驻留物理内存的页（**含与其他进程共享的页**），
  提交是记在该进程名下的**私有**提交量（含未驻留、不含共享）。所以会出现"已提交 < 已占用"——
  实测本机 124 个进程如此（`Shell Infrastructure Host` 工作集 52MB / 私有提交仅 9.3MB），
  这是系统层面的常态，不是读错；面板图例下方也写明了这一点

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

### 系统信息页（第 4 页）

格式照 `nonebot-plugin-status-zmd` 的「设备信息」规格网格：两列，标签在左（灰、12px）、值在右（深、13px），
行底一条虚线。十二项，按"行优先"排进两列：`主机名 | 操作系统`、`内核 | 运行时长`、`处理器 | 核心`、
`内存 | 显卡`、`磁盘 | 网络`、`数据目录 | 日志文件`。静态信息只采一次（采集端启动时 `collect_static`）。

- 操作系统名走注册表 `ProductName`/`DisplayVersion`：Win11 的 `ProductName` 仍写着 "Windows 10"，
  按 build ≥ 22000 校正（本机 → `Windows 11 Home China · 25H2`）；拿不到就退回 `platform`
- 运行时长是**系统** uptime（`psutil.boot_time()`），不是面板自己的运行时间；
  静态信息只在签名变化时重建，所以这一行由 0.5 秒的 UI tick 单独刷新
- 网络一行给本机 IPv4（跳过环回/虚拟网卡/169.254）与链路速率
- 采集端仍在 `sys` 里带 `proc_ver`（"exe · Python 版本 · pid"，用来判断壳拉起的是脚本还是打包的
  `backend.exe`），面板不再显示它

采集端仍保留 `/processes` 与 `/kill`（M3 的快速枚举与结束进程就在这里）：面板不再调用它们，
但命令行（`--kill-now --tree`）与 HTTP 直接调用照样可用。

### 进程枚举与结束进程（采集端能力，面板已不在 UI 里用）

- 枚举来自**一次** `NtQuerySystemInformation(SystemProcessInformation)`（403 个进程约 17~32ms）：
  名字、内存、线程数、CPU 时间都在同一条记录里，不需要按进程逐个 `OpenProcess`
- **CPU% 是两次采样的差**再按逻辑核数归一 —— 16 逻辑核上跑单线程满载，实测显示 6.3%（psutil 同刻 6.2%）
- 结束进程：单个或连子孙（`tree=1`，子孙先死）。**拦住**内核/关键进程（System/smss/csrss/wininit/services/
  lsass/winlogon/Registry/Memory Compression/fontdrvhost）与本工具自己的进程（父进程链，含壳）；
  权限不足（其他用户或受保护进程）时**按需提权**再试一次，和整理共用同一套 helper 机制

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
GET  /processes       进程表（pid/名字/内存/CPU%/线程/是否受保护）—— 面板已不再调用，命令行/HTTP 可用
POST /kill?pid=N      结束进程（tree=1 连子孙）；权限不足时按需提权 —— 同上
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
- 球面只有**占用率一个数**（提交额度不再给数字换色——两者不是一个口径）；提交额度由**右侧蓝条**表示。
  两侧的 1/4 装饰弧是计量条：左上橙 = 内存占用率、右下蓝 = 提交额度占用率，**水平线为零点**
  （橙从 9 点往 12 点涨、蓝从 3 点往 6 点涨），没占到的部分是**各自弧色的浅色版**
  （`#f3d6b9` / `#cbe0ed`）叠 0.35 透明度；主环未占用段是 `#f8f6e6` 叠 **0.55**（加 0.40 的衬底，
  整体约七成实、三成透——0.35 时太糊，看不清轨道）。
  **注意两道都要半透**：底槽下面还有一道"跑道"宽的描边带，它不透明的话底槽再透也透不出壁纸。
- 球窗口是 **180×180**、内容 160 居中：给点击放大（1.09）与悬停（1.05）留 10px 余量。
  装饰弧最外缘 76.65 DIP × 1.09 = 83.6 < 90，所以放大时不会顶到窗口边被裁掉一条。
- 中间内盘是"磨砂"观感：浅色高光渐变 + 一层细点纹理。真正的**背景模糊做不了**——DWM 的亚克力/毛玻璃
  按整窗矩形铺，会把当初在 WebView2 上吃过的"泛底"请回来（这也是换 WPF 的原因之一），
  所以这里用"高光 + 微点"近似磨砂，任意壁纸上都不会变成一块方雾。
- 整理动画是**先清零再回复**：环向 0 扫（0.6s）后停住等采集端结果（深度档要等 UAC，可能停很久），
  结果回来再从 0 涨到整理后的真实占用（0.52s）。
- 球的拖拽限制：全程用**物理像素** + `SetWindowPos`（Left/Top 是 DIP，混算在 125% 缩放下会漂），
  位置限制在**某一台显示器的工作区内**——多显示器时在每台显示器的合法范围里挑离目标最近的那个，
  所以能跨屏拖（按"窗口当前所在显示器"夹会永远跨不过去）。

## 目录

```
orb/                             WPF 壳（.NET 6）
  BallWindow.xaml(.cs)           球窗口：取数、单击跑轻度整理、拖拽/双击/右键、悬停
  BallVisual.cs                  电量环 + 中心粒子团（直绘，参数对应 ring.js）
  PanelWindow.xaml(.cs)          面板：三档整理 + 六张读数卡 + 系统信息规格网格
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