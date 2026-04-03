using System.IO;
using System.Reflection;

namespace ClipboardManager;

/// <summary>应用展示用版本与仓库地址（与 csproj 中 Version / RepositoryUrl 保持一致更佳）。</summary>
internal static class AppInfo
{
    /// <summary>源码、Issue 与 Release 页面。</summary>
    public const string GitHubUrl = "https://github.com/chaojimct/clipboardx";

    /// <summary>解析 <see cref="GitHubUrl"/>，得到 API 用的 owner / repo（须为 github.com 标准仓库根路径）。</summary>
    public static (string Owner, string Repo) ParseGitHubRepo()
    {
        var u = GitHubUrl.Trim().TrimEnd('/');
        const string prefix = "https://github.com/";
        if (!u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHubUrl 不是 github.com 仓库地址。");
        var rest = u[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash >= rest.Length - 1)
            throw new InvalidOperationException("GitHubUrl 格式应为 https://github.com/{owner}/{repo}。");
        var owner = rest[..slash];
        var repo = rest[(slash + 1)..];
        var q = repo.IndexOfAny(['/', '?', '#']);
        if (q >= 0)
            repo = repo[..q];
        if (owner.Length == 0 || repo.Length == 0)
            throw new InvalidOperationException("无法从 GitHubUrl 解析 owner/repo。");
        return (owner, repo);
    }

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

    /// <summary>
    /// 主程序文件名（含 .exe），与发布包内根目录 exe 一致；
    /// 用于「检查更新」解压校验与替换后重启；剪裁版为 ClipboardX-clipboard.exe / ClipboardX-filejump.exe。
    /// </summary>
    public static string PrimaryExecutableFileName
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                {
                    var f = Path.GetFileName(p);
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch
            {
                // ignore
            }

            var name = Assembly.GetExecutingAssembly().GetName().Name;
            return string.IsNullOrEmpty(name) ? "ClipboardX.exe" : name + ".exe";
        }
    }
}
