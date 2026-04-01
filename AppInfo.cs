using System.Reflection;

namespace ClipboardManager;

/// <summary>应用展示用版本与仓库地址（与 csproj 中 Version / RepositoryUrl 保持一致更佳）。</summary>
internal static class AppInfo
{
    /// <summary>源码、Issue 与 Release 页面。</summary>
    public const string GitHubUrl = "https://github.com/chaojimct/clipboardx";

    /// <summary>
    /// 与「设置 — 应用」中的版本、卸载弹窗、注册表 DisplayVersion 使用同一套规则，避免不一致。
    /// 优先 AssemblyInformationalVersion（去掉 + 号后的 SourceRevisionId 后缀）。
    /// </summary>
    public static string DisplayVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+', StringComparison.Ordinal);
                return plus >= 0 ? info[..plus] : info;
            }

            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }
}
