# Changelog

与 README「更新记录」同步维护。推送 `v*` 标签时，GitHub Release 正文中的 **更新内容** 由本节中对应 `## [版本]` 段落自动截取。

格式依据 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 的常见写法；日期为发布日（与 tag 推送日一致即可）。

## [1.3.6] - 2026-04-16

### 剪贴板 · 设置与粘贴

- **粘贴到目标窗口**：可在设置中选择写入剪贴板后向目标窗口模拟 **Ctrl+V** 或 **Shift+Insert**（默认 Ctrl+V；`settings.json`：`PasteSimulationMode`）
- **命令行 / 终端**：当配置为 Ctrl+V 时，若检测到粘贴目标为常见控制台或终端（如 `ConsoleWindowClass`、Windows Terminal 宿主类名，或 cmd、PowerShell、Windows Terminal、WSL、mintty 等进程），**临时**改用 **Shift+Insert**（不修改已保存的设置）

### 设置与管理

- **以管理员身份运行**：默认开启；在设置中切换该项并保存后，**自动重启**以匹配目标权限（UAC 提升、从已提升实例降权启动普通实例，或同权限下重启）
- **设置窗口前置**：打开设置时指定 **Owner** 为剪贴板浮层（相对居中），并在加载后 **激活窗口 + 抢前台**，避免设置窗落在置顶剪贴板面板或其它应用之后

## [1.3.5] - 2026-04-15

### 剪贴板 · 设置与外观

- **面板尺寸**：可在设置中自定义剪贴板弹窗**宽度**与**最大高度**（DIP，带合理范围校验），写入 `settings.json`（`PopupPanelWidth` / `PopupPanelMaxHeight`）
- **翻页条数**：新增「每次翻页条数」，与 **PgUp / PgDn、←→** 以及下方翻页快捷键一致，控制列表每次滚动的条目数（1～50，默认 8）
- **翻页快捷键**：改为**完整组合键**录制（须含 **Ctrl / Shift / Alt / Win** 之一，与呼出快捷键相同交互）；默认仍为 **Ctrl+-** 向上、**Ctrl+=** 向下；配置为 `PanelPageScrollUpModifiers` / `PanelPageScrollUpKey` 与 `PanelPageScrollDownModifiers` / `PanelPageScrollDownKey`；从仅保存单键的旧配置加载时会自动补全为 **Ctrl+** 语义
- **面板主键**：设置项恢复显示，可在 **Ctrl / Alt / Win / CapsLock** 间切换（与 **1～9** 快贴、**Tab** 短语过滤、**Enter** 等面板内组合逻辑一致）；帮助气泡中同步说明可在设置中更换

### 设置界面

- **修复**：「清空所有历史记录」按钮原 `Grid.Row` 超出 `RowDefinitions` 时，WPF 会为超出行隐式使用 `*` 行高，导致危险色按钮被**纵向拉满**；已补充 `RowDefinition`、修正行号并设 `VerticalAlignment="Top"`，避免误占满滚动区域

## [1.3.4] - 2026-04-10

### 剪贴板

- **FIFO / LIFO**：队列在他处已全部贴完后，可在**下一次**他处 **Ctrl+V / Shift+Insert** 时**自动切回「普通」**批量模式（默认开启）；设置中可关闭，避免长期停留在队列模式；兼容旧配置键 **`FifoAutoSwitchToNormalAfterQueueDone`**

## [1.3.3] - 2026-04-09

### 剪贴板

- **Alt 全局热键（VS Code 等）**：呼出/关闭面板时吞物理 Alt KeyUp 并注入 **Ctrl Down → Ctrl Up → Alt Up**；热键关面板时清空 `_ctxAlt*`，避免收尾分支被误判；`BeginInvoke(() => HidePopup())` 修复 **TargetParameterCountException**
- **批量队列**：`TryPushClipboardQueueHeadAsync` 在 `TrySetClipboardAsync` 重试间隙校验 **模式仍为 FIFO/LIFO 且队首未变**，避免切回普通或外部复制后仍被异步写回旧队首导致「粘贴成别的内容」

## [1.3.2] - 2026-04-09

### 剪贴板

- **批量队列修复**：普通模式下 Enter 粘贴选中项而非队列头；队列粘贴失败时恢复队列状态

## [1.3.1] - 2026-04-09

### 文件对话框跳转

- **微信（Weixin）**：`ResolveFileDialogHwndFromWindowOrAncestor` 增加 **`GetLastActivePopup`**，前台仍为应用主窗时也能对齐模态 `#32770`
- **到前台自动执行**：增加 **`EVENT_OBJECT_FOCUS`** 钩 + **`QuickMayBeUnderFileDialog`** 轻量过滤（部分宿主打开对话框时不重复发前台切换事件）
- **首次点击自动跳转**：焦点触发的路径下同步调用 **`UpdateFileJumpClickToNavigateArm`**；前台事件优先对已解析的对话框句柄武装鼠标钩
- **整理**：**`CollectCandidates`** 恢复单一 **Z 序推测（默认 +2）**，移除 **`CollectCandidatesAfterDialogReady`**；多处前台根窗口判断合并为 **`IsForegroundFocusOnFileDialogRoot`**

## [1.3.0] - 2026-04-08

### 剪贴板

- **批量队列**：支持 **普通 / FIFO / LIFO**；多选在 **FIFO/LIFO** 下 **Enter 入队**，在目标应用内每次 **Ctrl+V** / **Shift+Insert** 出队并推进剪贴板；顶栏模式 **Tag**、列表 **序号角标**、**托盘图标** 随模式换色（普通青绿 **#139493**、FIFO 蓝、LIFO 金），托盘叠 **F/L** 标记
- **模式切换**：默认 **Alt+/**（可在设置中修改）在面板外 **循环切换** 批量模式；顺序为 **普通 → LIFO → FIFO → 普通**，与面板顶栏一致
- **主题**：暗色面板贴近 **VS Code Dark+**；弹窗内列表 **选中 / 悬停** 混合色 **随当前批量模式主色**，与顶栏 Tag 统一；亮色仍为浅灰底 + 品牌强调色
- **交互**：底栏 **more** 与顶栏使用相同模式色 **pill**；快捷短语侧栏等延续不抢焦点输入；设置内帮助文案同步

## [1.2.9]

### 剪贴板

- **快捷短语**：设/改短语侧栏改为与主列表相同的 **键盘钩** 输入关键词，不再使用可聚焦 `TextBox`，避免抢宿主输入焦点
- **帮助**：主面板底栏 **more** 打开气泡展示完整快捷键说明（含修饰键组合）；**Esc** 可关闭

### 文件对话框跳转

- 根据前台线程 **GetGUIThreadInfo** 判断焦点；在 **另存为** 等对话框的 **Edit / RichEdit** 文件名框中时 **放行** 按键，便于边开跳转列表边编辑文件名；焦点在跳转面板内时逻辑不变

## [1.2.8]

### 数据与安装

- **单文件 / 绿色 zip**：便携数据根固定为 **`Environment.ProcessPath` 所在目录下的 `Data\`**，不再误写 `%TEMP%\.net\…`；首次启动可合并旧版 Temp 下 `Data`（若仍存在）
- **安装**：「安装到当前用户…」在框架依赖多文件发布时按当前主 exe 名检测主 DLL，剪裁版整机复制不再漏文件

## [1.2.7]

### 安装与数据

- 默认数据根 **`exe\Data\`**；仅当从 **`%LocalAppData%\Programs\ClipboardX`** 启动时使用 **`%LocalAppData%\ClipboardX`**；取消 Release 启动时自动复制到 Programs
- **托盘**：未安装显示「安装到当前用户…」，已安装显示「卸载…」；不依赖 **`ClipboardX.portable`**
- 复制到用户 Programs 时将 **`exe\Data\`** 中目标侧不存在文件合并到用户配置目录
- 按用户安装目录主 exe 名与剪裁版一致；Debug 安装菜单避免 CS0162

## [1.2.6]

### 文件跳转与兼容

- 「切回时自动同步路径」仅在前次前台为可解析路径的外部文件管理器时才用资源管理器目录驱动自动跳转
- 排除 **Internet Download Manager**（`IDMan.exe`）主界面误判为 `#32770` 导致的误触发与卡顿

## [1.2.5]

### 安装包

- Inno 安装前 .NET 8 桌面运行时检测增加与 `dotnet --list-runtimes` 一致的目录与 arm64 注册表后备路径

## [1.2.4]

### 安装包 / CI

- Inno `[Code]` 中函数内不使用 `const` 段，改为 `var`，修复 ISCC 报错

## [1.2.3]

### 安装包 / CI

- Inno `[Tasks]` 移除非法标志 `checked`，修复 ISCC 6.7.x unknown flag

## [1.2.2]

### 安装包 / CI

- 简体中文 `ChineseSimplified.isl` 随仓库分发，避免 Actions 上 Inno 无语言文件导致失败

## [1.2.1]

### 构建与剪裁版

- 修复 **FileJumpOnly** 排除 Sqlite 导致 CI 失败；Release 矩阵关闭 **fail-fast**
- **FileJumpOnly**：不注册剪贴板全局热键、不处理剪贴板消息，避免误占热键

## [1.2.0]

### 文件跳转

- 切回对话框时路径同步与列表刷新优化；快照采用分层短等

### 发布与设置

- 「检查更新」按产品前缀匹配 zip；Inno **setup** 与 **setup-self-contained** 双安装包
- 设置：暗色 **TabControl** 模板，改善标签对比度

## [1.1.6]

### 文件跳转与兼容

- 对话框到前台自动执行；跳转列表贴靠对话框
- 粘贴默认 **Shift+Insert**
- WPS 识别排除误匹配；修复 WPS 粘性跳转死循环

## [1.1.5]

### 其它

- 自定义文件对话框规则（导入/导出/探测向导）
- 剪贴板写入重试与诊断
- 图片粘贴回退为文件列表
- Q-Dir 等路径采集

## 更早版本

见 **[GitHub Releases](https://github.com/chaojimct/clipboardx/releases)** 与仓库 git 历史。
