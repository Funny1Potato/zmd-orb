# 第三方出处

## zmd-manager（终末地管理器）

- 仓库：<https://github.com/QinAnze/zmd-manager>（作者：QinAnze）
- 关系：本项目的**部分构建脚本、CI 配置、采集端与前端视觉实现**源自该项目
  （本项目 2026-09-26 起步时是在它的基础上改的）。2026-09-28 实测比对：

  | 文件 | 与 zmd-manager 的差异 |
  |---|---|
  | `build_exe.py` | 只差 6 行 |
  | `make_icon.py` | 30 行增 / 11 行删 |
  | `.github/workflows/build.yml` | 64 行增 / 40 行删 |
  | `collector/speed_collector.py` | 与它的 `collector/taskman_collector.py` 有 258 行共用（其余为后续扩展） |
  | 球面配色与结构 | `#ffe23d` / `#ecb063` / `#7fb2cc` 与"装饰弧 + 条纹"和它 `frontend/index.html` 同一套（观感源自《明日方舟：终末地》协议核心电量面板风格） |

- **许可状态：该项目没有 LICENSE 文件**，即默认**保留所有权利**。因此：
  - 根目录 `LICENSE` 里的 MIT 许可**只覆盖本项目自己写的部分**，不覆盖上面这些沿用来的文件；
  - **在取得其作者同意之前，本仓库不应对外公开分发**（转公开、对外发版都算）。
- **待办**：向作者取得许可；最理想是请他为 zmd-manager 补一个许可证（例如 MIT）。
  拿到之后把这里改成"经作者许可使用"并附上其许可全文；在那之前保持仓库私有。