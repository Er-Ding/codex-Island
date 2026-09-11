# 更新日志

记录每次修改的原因、范围、使用注意事项和验证状态。日期采用北京时间，最新记录在前；尚未发布的工作放在“未发布”下，正式发布时再归入实际版本。当前操作步骤统一维护在 [README](README.md)，后续维护约定见 [AGENTS.md](AGENTS.md)。

## 未发布

### 2026-09-11 · 建立更新日志与使用说明

- **原因**：权限、运行步骤和修改背景散落在聊天中，后续使用和维护容易遗漏。
- **修改**：新增本日志；在 README 补充任务跳转、首次使用权限、重新构建步骤和排查入口；校正早期拖动及数据读取说明。旧 Mac mini 交接文档标记为历史参考。
- **后续约定**：每次有意义的修改同步记录本日志；涉及使用方式、权限或安装步骤时，同步修改 README，规则写入 AGENTS.md。
- **验证状态**：仅文档与源码对照检查，未构建或运行测试。

### 2026-09-11 · 侧边聊天定位

**问题与原因**：用户反馈主会话可以打开，侧边聊天有时只能唤起 Desktop。核对本机 Desktop 代码后发现，侧边聊天是临时会话；普通 `codex://threads/<会话 ID>` 路由先读取已保存会话，查不到侧边聊天时就停止导航。

**修改内容**：

- 保留侧边聊天标记、所属主会话路径及首次提问生成的页签名称。页签名称与任务的自动标题可能不同。
- 点击时先请求打开所属主会话，再通过 macOS 辅助功能选择已有侧边页签；必要时使用“显示标签页”恢复已有右侧面板。
- 页签名称缺失时，在点击时重新请求一次任务快照。只保留用于定位的名称，不缓存完整提问上下文。
- 限定 Desktop 当前前台主窗口与主应用页面；精确匹配唯一名称，并检查页签选中状态。重名、窗口焦点变化或超时会显示原因。

**首次使用与限制**：

- **必须在“系统设置 → 隐私与安全性 → 辅助功能”中允许 Codex Island**，之后再次点击任务。详细步骤见 [首次使用与权限](README.md#首次使用与权限)。
- 如果侧边聊天放在底部面板，请先手动展开底部面板。当前代码只自动恢复已有的右侧标签页。
- 同名页签、缺少首条提问、特殊 Markdown/HTML 或超长提问可能无法匹配，届时需要手动选择。当前实现按已知页签名称定位，不能从辅助功能树直接取得侧边会话 UUID。
- 目前仅支持解析 `/local/<UUID>` 形式的主会话路径及其可识别的设备参数；其他所属页面会提示手动打开。
- 本次接口依据为本机 Desktop `26.908.31748`（build `8720`）。后续 Desktop 更新可能改变任务字段、页签标识或辅助功能结构；失效时需重新核对。

**涉及文件**：

- [TaskActivityClient.swift](Sources/CodexIsland/TaskActivityClient.swift)：保留导航元数据、点击时补取快照。
- [TaskActivityStore.swift](Sources/CodexIsland/TaskActivityStore.swift)：主会话路径解析、点击与取消流程。
- [TaskNavigator.swift](Sources/CodexIsland/TaskNavigator.swift)：将侧边聊天分流到专用导航。
- [TaskSideChatTitle.swift](Sources/CodexIsland/TaskSideChatTitle.swift)、[TaskSideChatNavigator.swift](Sources/CodexIsland/TaskSideChatNavigator.swift)：页签名称投影、辅助功能定位及反馈。

**验证状态**：完成源码与接口结构核对、差异格式检查；未构建，未运行测试，未实机验证侧边聊天跳转。此前用户反馈主会话可跳转，不能据此认定这次侧边聊天修改已通过验证。后续由用户按 [构建与重新运行](README.md#构建与重新运行) 执行，并回填实际结果。

### 2026-09-09 · 任务概览与跨应用会话跳转（补记）

**修改内容**：

- 缩略区增加任务数量与状态点，点击打开优先任务；展开列表支持逐行点击，并显示定位进度与反馈。
- 按任务来源尝试进入 Codex Desktop、VS Code 或 Terminal/iTerm2 的已有会话。原入口无法准确定位时，尝试使用 Desktop 打开同一会话并提示原因。
- 区分顶部任务点击、拖动与双击；鼠标按下时固定目标任务，避免刷新后打开另一个会话；调整顶部布局宽度。
- 补齐 `TaskVSCodeNavigator`，并将 Swift 无法导入的 `PROC_PIDPATHINFO_MAXSIZE` 宏改为明确的缓冲区大小，处理用户当时报告的两处编译错误。

**使用注意**：

- 任务列表读取 Desktop 已加载并同步的任务，需保持 Desktop 运行；它不是所有历史会话的列表。
- 终端定位可能要求允许 Codex Island 控制 Terminal/iTerm2，属于“自动化”权限，与侧边聊天的“辅助功能”权限不同。
- VS Code、终端和远端窗口定位依赖可确认的来源信息；终端页签已关闭、经 tmux/screen/SSH 中转或出现多候选时，可能回退 Desktop。

**涉及文件**：[任务状态客户端](Sources/CodexIsland/TaskActivityClient.swift)、[状态存储](Sources/CodexIsland/TaskActivityStore.swift)、[任务列表](Sources/CodexIsland/TaskActivityView.swift)、[缩略视图](Sources/CodexIsland/IslandView.swift)、[窗口控制](Sources/CodexIsland/IslandPanel.swift)、[顶部事件处理](Sources/CodexIsland/HeaderDragHandle.swift)、[尺寸计算](Sources/CodexIsland/IslandSizing.swift)、[统一导航](Sources/CodexIsland/TaskNavigator.swift)、[VS Code 导航](Sources/CodexIsland/TaskVSCodeNavigator.swift)、[终端导航](Sources/CodexIsland/TaskTerminalNavigator.swift)、[构建脚本](scripts/build-app.sh)。

**验证状态与背景**：当时修正源码后只做了静态检查，将重新构建留给用户；未留有后续构建通过的反馈。2026-09-11 用户反馈主会话可以跳转，同时指出侧边聊天失败。未找到用户明确说明“为什么先不提交”的记录，不能将待验证状态当成确定的未提交原因。

## 历史参考

本日志自 2026-09-11 开始维护，以上仅补记本次维护直接相关且有依据的改动。更早的改动以 Git 历史为准，不把提交存在当成验证通过。

| 日期 | 提交 | Git 提交说明 |
| --- | --- | --- |
| 2026-09-09 | `a6b368f` | constrain reback function & add task progression |
| 2026-09-09 | `9be9061` | update time apperance logical |
| 2026-09-09 | `350055c` | top view rectify |
| 2026-09-08 | `9611c82` | Moving Logical Changes |

早期运行记录见 README 的历史记录及 [Mac mini 历史交接](MAC_MINI_HANDOFF.md)。这些记录不代表上述未发布改动已经构建或验证。

## 后续记录格式

每项修改记录一次，后续补充进展直接更新对应条目，不必为每个文件或每轮对话另建日志。确实无关的字段写“不涉及”；没有做过的检查写“未执行”。

```markdown
### YYYY-MM-DD · 修改名称

- 原因：触发问题或需要实现的行为。
- 修改内容：改了什么，用户会看到什么变化。
- 涉及文件：关键实现文件的相对链接。
- 使用注意：首次使用权限、操作步骤、配置或兼容性变化。
- 验证状态：分别说明源码检查、构建、运行检查和用户反馈；没有执行的明确注明。
- 已知限制／后续事项：尚未解决的情况及下一步。
- 关联提交：提交后补充实际哈希，未提交时不虚构。
```
