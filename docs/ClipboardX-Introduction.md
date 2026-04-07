# ClipboardX：不抢焦点的剪贴板历史 + 开源的 Listary 式文件对话框跳转

## 为什么需要这样一个工具

Windows 上有两个高频效率场景长期被不同工具分别解决：

- **剪贴板历史**：Ditto、CopyQ 等
- **文件对话框路径跳转**：Listary、QuickSwitch、逍遥快跳等

ClipboardX 将这两个功能整合到一个轻量级的原生 Windows 应用中。下面分别展开说明它在这两个方向上的设计思路和差异。

---

## 一、剪贴板历史

### 1.1 不抢焦点——最核心的差异

在 IDE 里写代码、在聊天窗口打字、在浏览器填表单时，按下热键呼出剪贴板历史：

| | ClipboardX | Ditto | CopyQ |
|---|-----------|-------|-------|
| 弹窗后原窗口焦点 | **保持不变** | 被抢走 | 被抢走 |
| 输入法状态 | 不受影响 | 可能中断 | 可能中断 |
| 光标位置 | 不变 | 不变，但焦点回来后需要重新确认 | 同左 |

Ditto 和 CopyQ 的弹窗都是标准的激活窗口，呼出后系统焦点会转移过去。虽然粘贴操作完成后焦点会回到原窗口，但这个「焦点闪烁」在高频使用时很明显——特别是在 IDE 中，焦点离开编辑区可能触发自动补全窗口关闭、输入法状态重置等副作用。

ClipboardX 通过 `WS_EX_NOACTIVATE` 扩展窗口样式 + `WM_MOUSEACTIVATE` 消息拦截 + `SetWindowPos(SWP_NOACTIVATE)` 三层机制，确保弹窗从打开到关闭的全过程中，系统焦点始终停留在原窗口。

### 1.2 弹窗定位：跟随文本光标

| 定位方式 | ClipboardX | Ditto | CopyQ |
|----------|-----------|-------|-------|
| 文本光标旁 | 支持（多级降级） | 不支持 | 不支持 |
| 鼠标位置 | 支持 | 支持 | 部分支持 |
| 固定位置 | 可配置 | 默认 | 默认 |

ClipboardX 优先尝试获取当前窗口的文本插入符（Caret）位置，依次通过 `GetGUIThreadInfo`、`AttachThreadInput + GetCaretPos`、UI Automation `TextPattern` 三种方式获取。全部失败才回退到鼠标位置。

效果是：在编辑器中写代码时，剪贴板面板会出现在光标旁边，而不是屏幕中央或某个固定角落，减少视线往返。

### 1.3 搜索：直接打字 + 拼音匹配

| | ClipboardX | Ditto | CopyQ |
|---|-----------|-------|-------|
| 搜索触发 | 弹窗后直接打字 | 需要聚焦搜索框 | 需要聚焦搜索框 |
| 中文拼音搜索 | 全拼 + 首字母 | 不支持 | 不支持 |

ClipboardX 使用 `WH_KEYBOARD_LL` 低级键盘钩子直接捕获按键，弹窗出现后打字就是搜索，不需要先点击或 Tab 到搜索框。

拼音搜索基于 NPinyin 库，为每条历史记录预构建全拼 + 首字母索引。敲 `neirong` 或 `nr` 就能匹配到包含「内容」的条目——不需要切换输入法。对于中文用户，这是一个很实用的能力，Ditto 和 CopyQ 均不具备。

### 1.4 其他对比

| | ClipboardX | Ditto | CopyQ |
|---|-----------|-------|-------|
| 存储引擎 | SQLite（WAL 模式） | SQLite | 自有格式 / SQLite |
| 默认历史上限 | 2000 条 | 500 条 | 200 条 |
| 重复内容处理 | 自动去重并置顶 | 可配置 | 不自动去重 |
| 暗色主题 | Catppuccin Mocha，跟随系统 | 有限支持 | 需手写 CSS |
| 图片历史 | 支持，缩略图 64px 懒加载 | 支持 | 支持 |
| 文件列表历史 | 支持 | 支持 | 部分支持 |
| 技术栈 | WPF（.NET 8），3 个 NuGet 依赖 | C++ / MFC | C++ / Qt |
| 快捷短语 | 内置 | 需要通过分组实现 | 需要脚本 |

---

## 二、文件对话框跳转

这是 ClipboardX 的第二个核心功能：在系统「打开 / 另存为」对话框中，一键跳转到当前文件管理器正在浏览的目录。

这个领域长期以来的格局是：Listary 独占商业端，AHK 脚本工具（QuickSwitch、逍遥快跳）覆盖开源端。ClipboardX 提供了一个**原生开发的开源方案**。

### 2.1 工具分类

| 类别 | 工具 | 技术栈 | 跳转原理 |
|------|------|--------|----------|
| **商业闭源** | Listary | C++ 原生 | Shell 接口 + 模拟输入 |
| **AHK 脚本** | QuickSwitch、逍遥快跳 | AutoHotkey | 模拟键盘输入地址栏 |
| **原生开源** | ClipboardX | C# + C++ DLL | Shell 注入 `IShellBrowser::BrowseObject` + 多级回退 |

三类工具的根本差异在于**跳转深度**。AHK 脚本能做的事情局限在「模拟键盘操作」——找到地址栏、输入路径、按回车。这在多数场景下能用，但碰到地址栏不可见、被遮挡或非标准对话框时就会失败。

ClipboardX 和 Listary 则通过**进程注入 + Shell COM 接口**直接导航，不依赖 UI 状态，可靠性更高。

### 2.2 跳转机制对比

| | ClipboardX | Listary | QuickSwitch / 逍遥快跳 |
|---|-----------|---------|----------------------|
| **核心跳转方式** | `CreateRemoteThread` 注入 DLL → `IShellBrowser::BrowseObject` | 类似的 Shell 接口机制 | `ControlSetText` / `Send` 模拟地址栏输入 |
| **跳转副作用** | 无（等同于资源管理器树点击） | 无 | 可能闪烁地址栏、覆盖文件名 |
| **UI 线程安全** | `WH_GETMESSAGE` 钩子调度到正确线程 | 内部处理 | 不涉及（纯模拟输入） |
| **注入失败回退** | 自动降级为地址栏模拟 | 内部回退 | 无回退（只有模拟） |
| **可关闭注入** | 设置中可关（兼容杀软拦截） | 不可选 | 不涉及 |
| **架构适配** | x64 / x86 双 DLL，`IsWow64Process` 自动选择 | 内部处理 | 不涉及 |

ClipboardX 的 Shell 注入流程：`OpenProcess` → 架构检测 → `VirtualAllocEx` 写入 DLL 路径 → `CreateRemoteThread(LoadLibraryW)` 加载 DLL → 第二次远程线程调用导出函数 → DLL 内通过 `WM_USER+7` 获取 `IShellBrowser` → `BrowseObject` 导航。这套实现需要 C++ 原生代码，是 AHK 无法达到的。

### 2.3 文件管理器支持范围

| | ClipboardX | Listary | QuickSwitch | 逍遥快跳 |
|---|-----------|---------|-------------|---------|
| 资源管理器 | COM 接口 | 支持 | 支持 | 支持 |
| Total Commander | 专用消息 | 支持 | 专用消息 | 支持 |
| XYplorer | `WM_COPYDATA` + 脚本 | 支持 | `WM_COPYDATA` | 支持 |
| Directory Opus | `dopusrt.exe` 解析 XML | 支持 | 支持 | 支持 |
| FreeCommander | UIA 白名单 | 部分 | 不支持 | 不支持 |
| Double Commander | UIA 白名单 | 部分 | 不支持 | 不支持 |
| OneCommander | UIA 白名单 | 不支持 | 不支持 | 不支持 |
| Multi Commander | UIA 白名单 | 不支持 | 不支持 | 不支持 |
| Tablacus Explorer | UIA 白名单 | 不支持 | 不支持 | 不支持 |
| Files（商店版） | UIA 白名单 | 不支持 | 不支持 | 不支持 |
| 其他（xplorer² / SpeedCommander / fman 等） | UIA 白名单 | 不支持 | 不支持 | 不支持 |

ClipboardX 采用**分层策略**：对主流管理器走专用协议（最可靠），对长尾管理器走进程白名单 + UI Automation 广度搜索（尽力而为）。覆盖 15+ 种文件管理器，是同类工具中最广的。

### 2.4 WPS 对话框适配

WPS 的「打开 / 另存为」不是标准的 `#32770` 公共对话框，无法使用 `IShellBrowser` 接口。这是所有跳转工具的难点。

| | ClipboardX | Listary | AHK 系 |
|---|-----------|---------|--------|
| WPS 对话框识别 | 进程名 + 标题 + Qt 类名多重判断 | 部分识别 | 逍遥快跳有基础适配 |
| 跳转策略数 | 6 种（逐级降级） | 未知 | 1-2 种 |

ClipboardX 对 WPS 实现了六重降级：`ValuePattern.SetValue` → `ComboBoxEx32` 内嵌 Edit → 最底部 Edit 控件 → ReBar + F4 地址栏 → Alt+D 聚焦 → Ctrl+L + Unicode 输入。其中 ReBar + F4 的思路参考了逍遥快跳，但在其基础上增加了 UI Automation 和多种控件类型的适配。

### 2.5 独有能力

| 特性 | ClipboardX | Listary | AHK 系 |
|------|-----------|---------|--------|
| **Z 序推测** | 根据窗口 Z 序自动推断目标路径 | 有类似机制 | 不支持 |
| **首次点击自动跳** | 对话框打开后第一次点击即跳转 | 不支持 | 不支持 |
| **对话框到前台自动执行** | 检测到对话框到前台即自动弹列表/直跳，无需按键 | 有类似能力 | 不支持 |
| **切回时自动同步路径**（可关） | 从资源管理器等切回已打开对话框时刷新候选；可选自动跳到最近一次外部文件夹 | 视版本而定 | 一般无 |
| **路径收藏** | 支持，在跳转列表中管理 | 支持 | 逍遥快跳支持 |
| **记忆上次路径** | 自动记录每次对话框的目录 | 支持 | 部分支持 |

**首次点击自动跳转**是一个值得单独说明的设计：开启后，当文件对话框成为前台窗口，你在对话框内的第一次鼠标左键点击会自动触发跳转到列表首条路径（通常是 Z 序推测的结果）。同一个对话框窗口只会自动跳一次，手动热键不受影响。整个过程不需要按任何快捷键。

**对话框到前台自动执行**更进一步：开启后无需任何交互，检测到打开/保存对话框成为前台窗口时即自动采集路径，多候选弹出跳转列表（紧贴对话框并跟随移动），单候选直跳。同一对话框顶层窗口只自动处理一次，手动快捷键不受影响。

---

## 三、二合一的价值

| 传统方案 | ClipboardX |
|---------|-----------|
| Ditto/CopyQ + Listary/逍遥快跳 | 一个应用 |
| 2 个托盘图标 | 1 个 |
| 2 套配置 | 1 份 `settings.json` |
| 2 份内存占用 | 共享 UI 框架和钩子基础设施 |

这不是简单的功能堆叠。剪贴板面板和跳转选择器共用同一套弹窗样式、同一套键盘钩子架构、同一个主题引擎、同一个设置窗口。架构上的复用使得「二合一」的资源占用显著低于「两个独立工具」。

**完整版**主工程仅 3 个 NuGet 依赖（SQLite、NPinyin、Svg），没有 Electron，没有 WebView，是纯粹的原生 Windows 应用。发行还提供**仅剪贴板**、**仅文件跳转**等剪裁构建（见 Releases 资产名前缀），按需下载即可。

---

## 四、适合谁

- 觉得 Ditto / CopyQ 抢焦点不舒服的用户
- 需要中文拼音搜索剪贴板历史的用户
- 使用 Listary 但只用到文件对话框跳转功能、觉得其余功能用不上的用户
- 使用逍遥快跳但希望有更深度的跳转能力（Shell 注入）和更广的管理器支持的用户
- 不想同时装两个效率工具的用户
- 偏好开源软件的用户

---

## 下载与获取

**GitHub 仓库**：[https://github.com/chaojimct/clipboardx](https://github.com/chaojimct/clipboardx)

**下载最新版**：[Releases 页面](https://github.com/chaojimct/clipboardx/releases)（版本号以发布页为准，下列文件名中的 `*` 为版本号）

### 安装包（推荐完整版用户）

| 资产名模式 | 说明 |
|------------|------|
| `ClipboardX-*-setup.exe` | **Inno · 框架依赖**：安装包较小；需本机 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）。未检测到时安装向导可打开官方下载页，安装运行后再执行 setup。 |
| `ClipboardX-*-setup-self-contained.exe` | **Inno · 自包含**：体积较大，**无需**单独装 .NET；带开始菜单、卸载项，与自包含 zip 等价。 |

### 绿色 zip（任意目录解压）

| 资产名模式 | 说明 |
|------------|------|
| `ClipboardX-*-win-x64-no-runtime.zip` | 完整版单文件，体积小，需已装 **.NET 8 桌面运行时**。 |
| `ClipboardX-*-win-x64-self-contained.zip` | 完整版单文件，自带运行时。 |
| `ClipboardX-clipboard-*-win-x64-*.zip` | **仅剪贴板**（无文件跳转）。 |
| `ClipboardX-filejump-*-win-x64-*.zip` | **仅文件对话框跳转**（无剪贴板历史）。 |

「检查更新」会按**当前 exe 对应的产品前缀**选择上述 zip，避免多资产并存时误下其它变体。

**系统**：Windows 10 / 11 x64。解压包主文件名为 `ClipboardX.exe`（完整版）或 `ClipboardX-clipboard.exe` / `ClipboardX-filejump.exe`（剪裁版）。默认数据在 **你启动的 exe 同级 `Data\`**（绿色便携；**v1.2.8** 起单文件 zip 亦固定于此，不再写入 `%TEMP%\.net\…` 运行时解压目录）；托盘「安装到当前用户」后改用 **`%LocalAppData%\ClipboardX\`**（多 Flavor 目录名见 README）。详见仓库根目录 **README** 中「数据与日志」。

托盘右键可**检查更新**、查看关于信息；若使用共享运行时，更新会优先匹配较小的 no-runtime 包。
