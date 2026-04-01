# ClipboardX

**[下载最新版 (Releases)](https://github.com/chaojimct/clipboardx/releases)** · [源码](https://github.com/chaojimct/clipboardx)

一个轻量级 Windows 剪切板历史管理器，**核心特性：弹出窗口不抢焦点**。

## 演示

<p align="center">
  <b>剪贴板历史</b>（不抢焦点 · 实时 / 拼音搜索）<br/>
  <img src="static/clipx.gif" alt="剪贴板弹窗演示" width="720"/>
</p>

<p align="center">
  <b>文件对话框跳转</b>（热键与列表延时可在设置中配置，见下节）<br/>
  <img src="static/jumpx.gif" alt="文件对话框跳转演示" width="720"/>
</p>

## 下载与安装

1. 打开 **[Releases](https://github.com/chaojimct/clipboardx/releases)**，在最新版本 Assets 中选择 zip（版本号以发布页为准，例如 **1.1.0**）：
   - **`ClipboardX-1.1.0-win-x64-no-runtime.zip`** — 体积小，需本机已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)
   - **`ClipboardX-1.1.0-win-x64-self-contained.zip`** — 自带运行时，无需单独安装 .NET
2. 解压后双击 **`ClipboardX.exe`** 运行。若从压缩包等临时目录启动，程序会复制到 `%LocalAppData%\Programs\ClipboardX` 并在「应用和功能」中注册卸载项；托盘菜单 **关于** 可查看当前版本与主页链接。
3. **检查更新**：托盘图标右键 → **检查更新…**，会通过 GitHub Releases 比对版本；若有新版，会根据**当前进程是否使用本机已安装的 .NET**（检测 `System.Private.CoreLib` 是否从 `dotnet\shared\Microsoft.NETCore.App` 加载）自动选择 **no-runtime** 或 **self-contained** zip，缺一种则回退另一种；关闭程序后由脚本覆盖安装目录并自动重启。需可访问 GitHub（api.github.com 与 release 资源域名）。

### 首次运行出现「Windows 已保护你的电脑」（SmartScreen）

从互联网下载的未签名或低信誉 exe 常被 **Microsoft Defender SmartScreen** 拦截，属 Windows 默认策略，并非病毒判定。

**若你信任本仓库发布包：** 在蓝色提示窗口中点击 **「更多信息」**，再点击 **「仍要运行」** 即可。若从 zip 解压，也可右键 **`ClipboardX.exe`** → **属性** → 勾选 **「解除锁定」** 后确定，再启动。

分发版若未使用 **受信任 CA 的代码签名证书** 签名，无法从根上消除该提示；个人或小范围使用按上法操作即可。若希望对公网用户减少拦截，需在发布流程中对 exe 使用 **Authenticode 签名**（商用证书），新证书仍需时间积累信誉。

## 特性

- **焦点不丢失** — 基于 `WS_EX_NOACTIVATE`，弹出时原窗口保持输入焦点
- **实时搜索** — 弹出后直接输入文字过滤，键盘拦截不影响原窗口
- **多格式支持** — 文本、图片、文件列表全部记录，图片显示缩略图
- **全局热键** — 默认 Ctrl+`（反引号键），可在设置中自定义
- **快捷操作** — ↑↓ 导航、Enter 粘贴、Esc 关闭/清除搜索、Backspace 删搜索字符
- **智能定位** — 弹窗出现在文本光标或鼠标附近（可配置）
- **自动去重** — 重复内容自动提升到顶部
- **配置持久化** — 最大记录数、主题、透明度等写入 `%AppData%\ClipboardX`（从旧名 **ClipboardManager** 升级时会自动迁移 `settings.json`）
- **主题** — 支持跟随系统 / 亮色 / 暗色（Catppuccin Mocha）
- **文件对话框跳转** — 在系统「打开 / 保存」对话框处于前台时，按快捷键快速切到常用目录（详见下一节）

## 文件对话框跳转

在 **#32770 类公共文件对话框**（另存为、打开等）**处于前台**时，按 **文件对话框跳转键**（安装后默认 **Ctrl+G**，可在 **设置 → 文件对话框跳转键** 修改；勿与「呼出剪贴板」热键相同）即可使用。

### 行为概要

| 情况 | 表现 |
|------|------|
| 仅 1 个可用目标路径 | **直接跳转**，不弹出列表 |
| 多个候选路径 | 若在 **设置** 里为 **跳转列表延时** 配置了大于 0 的毫秒数，会先延时再弹出列表；**在延时内再按一次同一快捷键** 则取消列表并 **直接跳到当前预选项**（与列表中高亮一致） |
| 延时为 0 | **立即**弹出候选列表 |

候选路径来自：**当前资源管理器（或 Total Commander / XYplorer / Directory Opus 等）相关窗口**、**程序记忆的上次对话框路径**、以及你在列表里维护的 **收藏文件夹**。若没有任何可用路径，会提示先在外部文件管理器中进入目标目录。

### 列表内的操作（与主剪贴板面板相近）

- **方向键 / 翻页**：移动与翻页；**Enter** 确认跳转；**Esc** 先清空搜索再关闭。
- **主键 + 数字**（主键在设置「面板主键」中选择：Ctrl / Alt / Win / CapsLock）：跳可见项 1～9。
- **输入字母 / 拼音**：过滤列表（含收藏关键词）。
- **右键**：收藏目录、编辑关键词等。
- **点击外部**：与设置项 **「点击外部隐藏」** 一致——选「任意点击隐藏」时，点到跳转窗外来路会关闭列表（行为对齐剪贴板弹窗）。

### 与「Shell 深度跳转」的关系

公共对话框里换文件夹有两种方式：优先尝试 **将原生 DLL 注入宿主进程** 走 `IShellBrowser::BrowseObject`（命名空间级切换）；若无 DLL 或失败则 **回退为地址栏模拟**。单文件安装包会把 **`ClipboardXShellNavigate.dll`** / **`ClipboardXShellNavigate32.dll`** 打进 **同一 exe**，运行时解压到临时目录，无需单独拷贝 DLL。详见文末 **Shell 深度跳转** 与 **shell_navigate.log** 说明。

## 从源码运行

克隆仓库后：

```bash
dotnet run
```

调试构建会附带控制台窗口，便于查看日志；正式发布使用 Release 配置。工程文件仍为 `ClipboardManager.csproj`，输出二进制为 **ClipboardX**。

### 源码目录（简要）

| 目录 | 内容 |
|------|------|
| `Views/` | 主弹窗、设置、文件跳转选择器、`SharedPopupStyles.xaml` |
| `Models/` | `AppSettings`、剪贴板项、跳转列表行与候选、收藏条目 |
| `Services/` | 主题、GitHub 更新、剪贴板监听门禁、自启动、`AppInfo` |
| `FileJump/` | 文件对话框分类、路径收集、Shell 深度跳转与日志 |
| `Interop/` | Win32 P/Invoke |
| `Install/` | 按用户安装与卸载流程 |
| `Search/` | 拼音检索 |
| `Media/` | 托盘 SVG 栅格化图标 |
| `static/` | README 用演示动图（`clipx.gif`、`jumpx.gif`） |
| 根目录 | `App.xaml` / `App.xaml.cs`、`ClipboardManager.csproj`（当前工程版本 **1.1.0**） |

程序集根命名空间仍为 **ClipboardManager**，与文件夹解耦，避免大规模改引用。

## 自行编译发布

与 CI 接近的单文件发布示例（需已安装 .NET 8 SDK）：

```bash
# 先编原生 Shell 导航 DLL（可选；不编则单文件内不包含深度跳转能力）
powershell -ExecutionPolicy Bypass -File native/ShellNavigate/build.ps1

# 框架依赖 + 单文件（本机需 .NET 8）；IncludeAllContentForSelfExtract 把 ClipboardXShellNavigate*.dll 打进 exe
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o ./out/fdd

# 自带运行时 + 单文件（体积更大，无 dotnet 也可运行）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./out/sc
```

单文件运行时会把捆绑文件解压到临时目录；代码用 `AppContext.BaseDirectory` 解析 `ClipboardXShellNavigate*.dll`，与上述设置一致。

打标签推送后，GitHub Actions 会按 `v*` 标签构建并上传到 Releases。

## 环境与要求

| 场景 | 要求 |
|------|------|
| 使用 **no-runtime** 安装包 | Windows 10/11，并已安装 **.NET 8**（桌面）运行时 |
| 使用 **self-contained** 安装包 | Windows 10/11 |
| 本地开发 | .NET 8 SDK |

## Shell 深度跳转（可选原生 DLL）与日志

使用 **文件对话框跳转**（默认快捷键 **Ctrl+G**，见上文「文件对话框跳转」）换目录时，若运行目录下存在 **`ClipboardXShellNavigate.dll`**（及 32 位宿主用的 **`ClipboardXShellNavigate32.dll`**；**单文件发布** 时已打包进 exe，解压后对相关路径可见），会尝试 **注入宿主进程并 `IShellBrowser::BrowseObject`**；失败则自动回退到地址栏模拟。

- **编译原生 DLL**：已装 VS / Build Tools（含 **MSVC** + **Windows SDK**）时，在仓库根目录执行  
  `powershell -ExecutionPolicy Bypass -File native\ShellNavigate\build.ps1`  
  若本机无工具链可加参数 **`-InstallBuildTools`**（通过 winget 安装 VS 2022 Build Tools 的 C++ 工作负荷，耗时较长）。生成物会复制到 `bin\Release\net8.0-windows\` 与 `bin\Debug\net8.0-windows\`（便于 Debug 运行）。若 MSBuild 报工具集错误，可编辑 `native\ShellNavigate\ClipboardXShellNavigate.vcxproj` 将 `PlatformToolset` 的 **v143** 改为 **v142**（仅当已安装 VS 2019 工具集时）或反之，与本地安装一致。仓库默认 **v143**（VS 2022）。
- **诊断日志（注入端 + 被注入端）**：统一写入  
  **`%LocalAppData%\ClipboardX\shell_navigate.log`**（UTF-8，与 `ClipboardX` 配置目录同根）。注入过程由 **inject** 前缀记录；在宿主进程内执行的 **native** 前缀由同名 DLL 写入，便于对照 `BrowseObject`  HRESULT。
