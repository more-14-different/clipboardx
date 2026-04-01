# ClipboardX

**[下载最新版 (Releases)](https://github.com/chaojimct/clipboardx/releases)** · [源码](https://github.com/chaojimct/clipboardx)

一个轻量级 Windows 剪切板历史管理器，**核心特性：弹出窗口不抢焦点**。

## 下载与安装

1. 打开 **[Releases](https://github.com/chaojimct/clipboardx/releases)**，在最新版本 Assets 中选择 zip：
   - **`ClipboardX-x.x.x-win-x64-no-runtime.zip`** — 体积小，需本机已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)
   - **`ClipboardX-x.x.x-win-x64-self-contained.zip`** — 自带运行时，无需单独安装 .NET
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

## 从源码运行

克隆仓库后：

```bash
dotnet run
```

调试构建会附带控制台窗口，便于查看日志；正式发布使用 Release 配置。工程文件仍为 `ClipboardManager.csproj`，输出二进制为 **ClipboardX**。

## 自行编译发布

与 CI 接近的单文件发布示例（需已安装 .NET 8 SDK）：

```bash
# 框架依赖 + 单文件（本机需 .NET 8）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true -o ./out/fdd

# 自带运行时 + 单文件（体积更大，无 dotnet 也可运行）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./out/sc
```

打标签推送后，GitHub Actions 会按 `v*` 标签构建并上传到 Releases。

## 环境与要求

| 场景 | 要求 |
|------|------|
| 使用 **no-runtime** 安装包 | Windows 10/11，并已安装 **.NET 8**（桌面）运行时 |
| 使用 **self-contained** 安装包 | Windows 10/11 |
| 本地开发 | .NET 8 SDK |
