# 第三方出处与重写记录

## 一、结论

本项目（zmd-orb）2026-09-26 起步时是在 [zmd-manager](https://github.com/QinAnze/zmd-manager)
（作者 QinAnze，**没有 LICENSE 文件** = 默认"保留所有权利"）的基础上改的。2026-09-28 做了一轮核对
（行级比对 → token 级比对 → 跨语言的字符串/常数取证），并把**沿用的代码全部重写成独立实现**，
每项都带验证（见第二节）。结论：

**本仓库现在没有需要向 zmd-manager 取得许可的部分**，根目录 `LICENSE` 的 MIT 覆盖整个仓库。
保留的署名是礼貌性的。

仍然算"同源"的只剩三类，都不构成需要授权的表达：

| 类别 | 例子 | 为什么不算 |
|---|---|---|
| 配置样板 | `build_exe.py` 的 PyInstaller 命令行参数、`.github/workflows/build.yml` 的 Actions 标准步骤 | 命令行参数与标准步骤属于配置/事实用法 |
| 接口口径 | `orb/MemoryApi.cs` 与采集端的 JSON 键名、HTTP 路径、`X-Zmd-Orb` 头 | 接口是兼容性要求（壳要能读采集端的数据） |
| 设计口径 | 选自《明日方舟：终末地》协议核心电量面板的那几支配色、页面结构与单位文案 | 配色与观感是设计，不是可保护的表达 |

## 二、当天重写了什么、怎么验的

| 对象 | 原来的情况 | 现在 | 验证 |
|---|---|---|---|
| `collector/speed_collector.py` 的设备/系统信息段 | 与它 `collector/taskman_collector.py` 有 **152 token / 29 行**的逐字连续块（盘↔分区的 `ASSOCIATORS` 嵌套 PowerShell），另有 9 个 ≥25 token 的块 | 按功能独立重写：`ps_text`/`ps_json`/`ps_rows`/`cim` 桥；按设备拆成 `_cpu_info`/`_mem_info`/`_gpu_info`/`_net_info`/`_disk_info`；磁盘拓扑改成"两条关联类各查一次 + 在 Python 里按盘号拼"；显卡回落改成"PowerShell 只出数、Python 侧聚合" | 新旧 `collect_static()` **26 个键逐键一致**（只有 `proc_ver` 里的 pid 该变）；快照 A/B 硬盘段逐字相同；把重写版起在 8911 走完 `/health` → `/snapshot?want=procs,dev` 轮询（显卡、磁盘活动率、进程显示名与窗口标题、自测全出得来）；真实壳里冷启动日志干净；同机 `sample_procs` 计时：旧 11.64 s / 新 9.74 s |
| `frontend/ring.js` | `polar()` 等 **79 token** 的逐字块；粒子团与它共用一个旋转公式 | `pointOn`/`arcD`/`halton`/`_puff` 独立写法 | 用 node 把两版都跑起来比：取点 515 个角度、弧 path 726 组、`buildRing` 生成的 8 个节点全部属性、各占用率下的 `d`/`stroke`（含 70%/88% 两个换色点）全一致，差异只有浮点末位 ≤1.3e-13 |
| `orb/GaugeVisual.cs` | 整块是它 `index.html` 大圆环画法的逐项翻译（`202/28`、`221/183/3`、`189/215`、`151/24`、`150/18`、`126`、**绘制顺序**、clamp 与 `*3.6`） | 静态壳（冻结的 `DrawingGroup`）与动态黄弧两层；三档色笔与每档两层辉光全部预建；条纹改用 `Ring.Sector` + 自写定种子 xorshift32；删掉两处**逐像素证明看不见**的死绘制 | 离屏渲染分区对拍（470×470）：装饰弧内缘/外缘、环内（黄弧与其辉光、衬环、轨道、内盘）、环外、带内非弧角度 **全部 0 差异**；只有条纹纹理本身（换伪随机的必然结果）与弧端 7 个抗锯齿像素不同 |
| `orb/BlobVisual.cs` | 它 `drawBlob` 的逐项翻译（650 点、`126×(0.79+0.12×呼吸+0.09×噪声)`、点半径 `0.7+depth×1.2`、透明度 `0.10+depth×0.40`、`rgb(96,96,92)`、斐波那契 `2.39996`） | 重写：Halton 低差异序列撒点、`Puff`/`Bucket` 拆成独立成员、颜色走 `Ring.BlobRgb` | 与基线的像素差落在"同一版本渲染两次"的噪声底内（最大通道差 41 vs 底 40；差>8 的像素 7.00% vs 7.26%） |
| `orb/BallVisual.cs` 的粒子团 | 与 `ring.js` 的 `_draw()` 逐项对应 | 同一套撒法与写法（观感参数保留） | 同上（最大 46 vs 底 47；差>8 的 3.52% vs 3.46%）；非透明像素数与基线相同 |
| `orb/Ring.cs` 的 `Polar`/`Arc`/`Sector`/`ArcColor` | `(deg−90)` 再取 `cos/sin`，与它 `polar()`/`arcPath()` 逐行等价 | 按 `sin/cos` 直接组合、`sweep` 变量、`if/return` 分档；新增共享的 `Halton` | 覆盖在上面两项的逐像素对拍里 |
| `orb/PanelWindow.xaml(.cs)` 页1/页2 的样式 | 把它 `index.html` 的 CSS 逐条翻成 XAML（`18px/1.3px/#bdbdb8` 点阵、13 个调色板值、`38×38` 导航、状态胶囊、报告开关、详情覆盖页、`perf-head-strip`…） | 重新组织成我们自己的设计令牌（资源键 27 → 115 个，引用一并更新） | 结构检查：`x:Name` 80 个一个不差、绑定与事件接线集合一致；颜色字面量（44 处/37 种）与数字字面量（741 处/75 种）两版**完全一致**；面板截图分块对比：所有差异都落在"有数据/文字"的格子里，外壳（顶栏、点阵、徽标、页签、卡片边框、底栏）**逐像素为 0** |
| `orb/IconFactory.cs` / `orb/IconView.cs` | 图标表**逐字抄**自它 `ICONS`（且不是纯 Feather：一部分是被改过的变体，不能靠"署名 Feather"处理）；分类规则 10 条正则逐字相同 | 图标整体换成官方 **Lucide** 原始数据（24 个），分类规则改成自写的"有序关键词组" | 渲染 24 个图标的对照图逐个核对；编译 0 错误 |

`make_icon.py` 已无 ≥12 token 的重合块；`build_exe.py`（两边只差 6 行）与
`.github/workflows/build.yml` 的重合部分只剩 PyInstaller 参数与 Actions 标准步骤 —— 都未再改动。

## 三、第三方组件

| 组件 | 用途 | 许可 | 处理 |
|---|---|---|---|
| **Lucide Icons** | 界面 24×24 描边图标（`orb/IconFactory.cs` 的路径数据） | **ISC**；其中衍生自 Feather 的那批为 **MIT**（© Cole Bemis） | 许可全文与署名随仓库和发布包一起分发：`THIRD-PARTY-LICENSES.txt`（CI 暂存步骤与安装包 `[Files]` 都已带上） |
| Inno Setup 中文语言包 | 安装包向导的中文（`installer/ChineseSimplified.isl`） | 社区翻译（维护者 Zhenghan Yang / kira-96） | **待确认其许可条款**；它只在编译安装包时用到，不参与程序运行 |

## 四、致谢

界面风格来自《明日方舟：终末地》的协议核心电量面板；项目起步与早期结构参考了
zmd-manager（<https://github.com/QinAnze/zmd-manager>），在此致谢。