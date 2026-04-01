# ClipboardX

**[下载最新版 (Releases)](https://github.com/chaojimct/clipboardx/releases)** · [源码](https://github.com/chaojimct/clipboardx)

轻量级 Windows **剪贴板历史**工具：**弹出面板不抢焦点**（`WS_EX_NOACTIVATE`）。

## 演示

<p align="center">
  <b>剪贴板历史</b>（不抢焦点 · 实时 / 拼音搜索）<br/>
  <img src="static/clipx.gif" alt="剪贴板弹窗演示" width="720"/>
</p>

<p align="center">
  <b>文件对话框跳转</b>（热键与延时在设置中可调）<br/>
  <img src="static/jumpx.gif" alt="文件对话框跳转演示" width="720"/>
</p>

## 下载与安装

1. 打开 **[Releases](https://github.com/chaojimct/clipboardx/releases)**，在 Assets 中选 zip（版本号以发布页为准，例如 **1.1.4**）：
   - **`ClipboardX-*-win-x64-no-runtime.zip`** — 体积小，需已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)
   - **`ClipboardX-*-win-x64-self-contained.zip`** — 自带运行时，无需单独装 .NET
2. 解压后运行 **`ClipboardX.exe`**。从临时目录启动时，程序会复制到 `%LocalAppData%\Programs\ClipboardX` 并可在「应用和功能」中卸载；托盘 **关于** 可查看版本与主页。
3. **检查更新**：托盘右键 → **检查更新…**，对比 GitHub Releases；若有新版，会按当前是否使用共享运行时选择 **no-runtime** 或 **self-contained** 包，关闭程序后覆盖并重启。需可访问 GitHub（含 `api.github.com`）。

### SmartScreen「Windows 已保护你的电脑」

自解压/互联网下载的 exe 若未做 **Authenticode 签名**，SmartScreen 可能拦截，属信誉策略而非「报毒」。

**若你相信本仓库发布包：** 点 **更多信息** → **仍要运行**；或对 exe **右键 → 属性 → 解除锁定** 后再开。对公网分发减少提示需购买受信证书并持续积累信誉；个人使用按上法即可。

## 特性（剪贴板）

- **不抢焦点** — 原窗口保持输入焦点，适合 IDE / 聊天等场景  
- **实时搜索** — 弹出后直接输入过滤；热键与键盘逻辑在独立钩子里处理  
- **多格式** — 文本、图片、文件列表；图片可显示缩略图  
- **全局热键** — 默认 **Ctrl+`**（反引号），可在设置修改  
- **快捷操作** — ↑↓、Enter 粘贴、Esc、Backspace 删搜索字符  
- **弹窗位置** — 文本光标或鼠标附近（可配置）  
- **去重** — 重复内容提升到顶部  
- **主题** — 跟随系统 / 亮 / 暗（Catppuccin Mocha）  
- **配置** — 设置写入 `%AppData%\ClipboardX`（从旧名 **ClipboardManager** 迁移时会带上 `settings.json`）；**普通剪贴板历史**持久化在 `%LocalAppData%\ClipboardX\clipboard_history.db`（SQLite）  
- **文件对话框跳转** — 见下节（默认 **Ctrl+G**，勿与呼出剪贴板热键重复）

## 文件对话框跳转

在以下窗口**处于前台**时，按 **文件对话框跳转键**（默认 **Ctrl+G**，**设置**里可改）使用：

| 类型 | 说明 |
|------|------|
| 系统公共对话框 | Win32 **`#32770`** 类「打开 / 保存」等 |
| WPS 等 | 套件自带「打开文件」「另存为」等 **非** 标准公共对话框（文字 / 表格 / 演示等） |

- **WPS**：用界面自动化、面包屑、**ReBar + F4** 地址栏等策略（思路参考 [XiaoYao_QuickJump](https://github.com/lch319/XiaoYao_QuickJump)），**不**做 Shell DLL 注入。  
- **系统公共对话框**：可注入宿主走 `IShellBrowser::BrowseObject`；失败则回退地址栏模拟。可在 **设置** 中关闭 **「Shell 注入跳转」**（默认开启），遇杀软拦截时只用模拟方式，兼容更好。

### 行为概要

| 情况 | 表现 |
|------|------|
| 没有可用路径 | **静默**（无提示） |
| 仅 1 条候选 | **直接跳转**，不弹列表 |
| 多条候选、延时大于 0 ms | 先延时再弹列表；**延时内再按一次同一快捷键** → 直接跳当前预选项 |
| 多条候选、延时为 0 | **立即**弹列表 |
| **点击后自动跳转**（设置默认开） | 对话框成为前台后，**第一次**在框内点左键即按列表**首条**路径跳转；**同一对话框窗口存活期内只自动跳一次**（关掉再开才再来），手动 **Ctrl+G** 不受影响 |

**路径来源（摘录）：** 资源管理器；**Total Commander / XYplorer / Directory Opus**（与 [QuickSwitch](https://github.com/gepruts/QuickSwitch) 同类专用通道）；以及 FreeCommander、Double Commander、OneCommander 等**白名单进程**上的**浅层 UI 自动化**（无官方 API 时尽力而为，双栏可能只取一侧）；另含**记忆的上次路径**、列表**收藏**。无任何路径则本次按键无效——可先在外部管理器进到目标目录再试。

### 跳转列表里的操作

与主剪贴板面板类似：**↑↓** 选择，**←→** 翻页，**单击行** 或 **Enter** 确认跳转，**Esc** 清搜索 / 关闭，**主键+1～9** 快速跳可见项，输入字符过滤（含收藏关键词），**右键** 收藏与管理。**点击外部**是否关列表与设置 **「点击外部隐藏」** 一致。

## 从源码运行

```bash
dotnet run
```

Debug 带控制台；正式发布用 **Release**。工程文件 **`ClipboardManager.csproj`**，输出名 **ClipboardX**，根命名空间仍为 **ClipboardManager**。

### 源码目录（简要）

| 目录 | 内容 |
|------|------|
| `Views/` | 主弹窗、设置、跳转选择器、`SharedPopupStyles.xaml` |
| `Models/` | `AppSettings`、剪贴板项、跳转与收藏模型 |
| `Services/` | 主题、更新、剪贴板门禁、自启动、`AppInfo` |
| `FileJump/` | 对话框分类、路径收集、Shell 跳转与日志 |
| `Interop/` | Win32 P/Invoke |
| `Install/` | 按用户安装 / 卸载 |
| `Search/` | 拼音检索 |
| `Media/` | 托盘 SVG 栅格化 |
| `static/` | 演示动图 |
| 根目录 | `App.xaml`、`ClipboardManager.csproj`（版本见 csproj） |

## 自行编译发布

需 **.NET 8 SDK**。与 CI 类似示例：

```bash
# 可选：先编 Shell 导航原生 DLL（跳过则深度跳转能力受限）
powershell -ExecutionPolicy Bypass -File native/ShellNavigate/build.ps1

# 框架依赖 + 单文件（本机需 .NET 8）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o ./out/fdd

# 自带运行时 + 单文件
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./out/sc
```

单文件会把内嵌 DLL 解压到临时目录；运行时通过 `AppContext.BaseDirectory` 加载 **`ClipboardXShellNavigate*.dll`**。打 **`v*`** 标签推送后，Actions 会构建并上传 Releases。

## 环境与要求

| 场景 | 要求 |
|------|------|
| no-runtime 包 | Windows 10/11 + **.NET 8** 桌面运行时 |
| self-contained 包 | Windows 10/11 |
| 开发 | .NET 8 SDK |

## Shell 深度跳转与日志（可选）

仅影响**系统公共**对话框：若运行目录（或单文件解压目录）可见 **`ClipboardXShellNavigate.dll`**（及 32 位宿主用的 **`ClipboardXShellNavigate32.dll`**），会尝试注入并 **`BrowseObject`**；失败则回退 UI/地址栏。**设置 → Shell 注入跳转** 可关掉注入。WPS 等自定义框**从不**注入。

- **编译 DLL**：`powershell -ExecutionPolicy Bypass -File native\ShellNavigate\build.ps1`（需 MSVC + Windows SDK；可加 **`-InstallBuildTools`** 用 winget 装 VS Build Tools，较慢）。产物会复制到 `bin\...\net8.0-windows\`。若工具集报错，可改 `native\ShellNavigate\ClipboardXShellNavigate.vcxproj` 中 `PlatformToolset`（**v143** / **v142**）与本地一致。
- **日志**：**`%LocalAppData%\ClipboardX\shell_navigate.log`**（UTF-8）。**inject** 为注入端；**native** 为 DLL 在宿主内写入，便于对照 HRESULT。WPS 等回退时也可能写入 **wps** 等前缀行。
