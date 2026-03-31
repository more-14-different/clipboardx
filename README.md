# ClipboardManager

一个轻量级 Windows 剪切板历史管理器，**核心特性：弹出窗口不抢焦点**。

## 特性

- **焦点不丢失** — 基于 `WS_EX_NOACTIVATE`，弹出时原窗口保持输入焦点
- **实时搜索** — 弹出后直接输入文字过滤，键盘拦截不影响原窗口
- **多格式支持** — 文本、图片、文件列表全部记录，图片显示缩略图
- **全局热键** — 默认 `Ctrl+``，可在设置面板自定义
- **快捷操作** — ↑↓ 导航、Enter 粘贴、Esc 关闭/清除搜索、Backspace 删搜索字符
- **智能定位** — 弹窗出现在文本光标/鼠标附近
- **自动去重** — 重复内容自动提升到顶部
- **配置面板** — 最大记录数（默认 2000）、快捷键均可配置，持久化到 `%APPDATA%`
- **暗色主题** — Catppuccin Mocha 配色

## 使用

```bash
dotnet run
```

发布为单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 要求

- .NET 8 SDK
- Windows 10/11
