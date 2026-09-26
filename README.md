# 终末地加速球 · zmd-orb

常驻悬浮球：一键**整理内存缓存页**，同时能当任务管理器用。环的样式沿用 `zmd-manager`（终末地管理器）的电量环。

> **状态：M0 骨架。** 目前只有：双窗口壳（透明置顶球 + 面板）、采集端的 `/snapshot`、球的静态环与粒子团。
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

## 架构

| 层 | 内容 |
|---|---|
| 壳 | Tauri 2：`ball`（透明/置顶/不进任务栏的球）+ `panel`（任务管理器），托盘由 `tray-icon` 特性提供 |
| 内核 | `collector/speed_collector.py`：psutil + ctypes，本地 API `127.0.0.1:8910`，兼伺服前端静态页 |
| 前端 | `frontend/`：单文件 HTML + 内联 SVG + Canvas，零前端依赖 |

本地 API 端口用 **8910**，避开 zmd-manager 的 8899，两个工具可以同时开。

## 构建（在 CI 上）

本机没有 Rust 工具链，Tauri 壳由 GitHub Actions 编译（`.github/workflows/build.yml`）：

1. `python build_exe.py` → `dist/backend.exe`（PyInstaller 单文件）
2. 拷到 `tauri/src-tauri/backend.exe`，作为 Tauri 资源随安装包落地到 `resources/`
3. `npx @tauri-apps/cli@2 build --bundles nsis` → NSIS 安装包

本地能跑的部分（前端精修用）：

```
python collector/speed_collector.py --no-gui
# 然后浏览器打开 http://127.0.0.1:8910/ball.html
# 球需要透明底才看得准，用 http://127.0.0.1:8910/dev.html 预览
```

## 开发约定

- **壳一次写到位，行为尽量放前端（Tauri JS window API）与 Python。** 因为本机不能编译 Rust，壳层每改一次都要过 CI，
  所以窗口行为、动画、阈值都在前端/内核里调，Rust 只负责窗口与进程生命周期。
- 新增 Rust 侧改动前先确认真的绕不过去。

## 目录

```
collector/speed_collector.py    本地采集/清理服务（psutil + ctypes）
frontend/ball.html              悬浮球（电量环 + 中心粒子团）
frontend/panel.html             任务管理器面板
frontend/dev.html               前端开发预览页（模拟透明底、球径切换、动画回放）
frontend/ring.js                环形几何/配色（从 zmd-manager 移植）
frontend/api.js                 取数薄层（fetch 127.0.0.1:8910）
tauri/src-tauri/                Tauri 2 壳（tauri.conf.json / capabilities / src/main.rs）
build_exe.py                    打 backend.exe
.github/workflows/build.yml     CI：出 NSIS 安装包
```