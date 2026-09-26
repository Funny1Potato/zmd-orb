# 终末地加速球 · zmd-orb

常驻悬浮球：一键**整理内存缓存页**，同时能当任务管理器用。环的样式沿用 `zmd-manager`（终末地管理器）的电量环。

> **状态：M0 骨架。** 目前只有：双窗口壳（透明置顶球 + 面板）、采集端的 `/snapshot`、球的电量环与粒子团。
> 清理逻辑（M1）、面板进程表（M3）、托盘与开机自启（M4）都还没实现。

## 它做什么、不做什么

Windows 上没有"释放内存"的魔法。所谓加速球做的是两件事：把冷进程的工作集换出去、把待命列表（文件缓存）清空。
被换出的页在进程下次访问时会**硬缺页**从 pagefile 读回来——所以它不产生内存，只是把内存从"占用"搬到"可用"。

因此本工具的显示口径刻意保守：

- 文案用"**整理 X GB 缓存页**"，不写"释放内存"
- 同时显示代价：**硬缺页率**（`\Memory\Pages Input/sec`）、**页文件使用率**、**提交压力**
  （提交额度才是真会把程序打崩的东西，物理内存百分比看不出这个）
- 自动整理盯 **free + zero page list**，而不是 `available`——`available` 把待命列表算作可用，
  清空待命列表后它上升，但内存并没有变多

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
  BallWindow.xaml(.cs)           球窗口：取数、整理动画、拖拽/单击、悬停
  BallVisual.cs                  电量环 + 中心粒子团（直绘，参数对应 ring.js）
  PanelWindow.xaml(.cs)          任务管理器面板（M3 加进程表）
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