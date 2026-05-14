using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinForms = System.Windows.Forms;

namespace ClipboardManager;

/// <summary>通过 GitHub Releases 检查版本、下载 zip 并在退出进程后替换文件并重启。</summary>
internal static class GitHubUpdateService
{
    private const string SelfContainedSuffix = "win-x64-self-contained.zip";
    private const string NoRuntimeSuffix = "win-x64-no-runtime.zip";

    private static readonly Lazy<HttpClient> HttpLazy = new(CreateHttpClient);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipboardX/{AppInfo.DisplayVersion} ({AppInfo.GitHubUrl})");
        c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return c;
    }

    private static HttpClient Http => HttpLazy.Value;

    internal readonly struct ReleaseAssetInfo
    {
        public string Name { get; init; }
        public string DownloadUrl { get; init; }
        public long Size { get; init; }
        /// <summary>对应 CI 中的 no-runtime（框架依赖）zip；否则为 self-contained。</summary>
        public bool IsNoRuntimeVariant { get; init; }
    }

    internal readonly struct LatestReleaseInfo
    {
        public string TagName { get; init; }
        public string Body { get; init; }
        public ReleaseAssetInfo ChosenAsset { get; init; }
    }

    public static async Task<LatestReleaseInfo> FetchLatestReleaseAsync(CancellationToken ct = default)
    {
        var (owner, repo) = AppInfo.ParseGitHubRepo();
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"GitHub API {(int)resp.StatusCode}：{TruncateNote(json, 200)}");

        var doc = JsonSerializer.Deserialize<GitHubReleaseDoc>(json);
        if (doc?.TagName is not { Length: > 0 } tag)
            throw new IOException("发行版数据异常：缺少 tag_name。");

        var chosen = PickZipAsset(doc.Assets, PreferNoRuntimeZip());
        if (chosen == null)
            throw new IOException(
                "该发行版未找到与当前程序匹配的 win-x64 zip（no-runtime / self-contained）。发行页上需存在与主 exe 前缀一致的包。");

        var body = doc.Body ?? "";
        return new LatestReleaseInfo
        {
            TagName = tag,
            Body = body,
            ChosenAsset = chosen.Value,
        };
    }

    /// <summary>
    /// 当前进程是否从本机「dotnet\shared\Microsoft.NETCore.App」加载 CoreCLR（与 no-runtime / FDD 发行包一致）；
    /// 否则视为 self-contained，更新时优先下大包。
    /// </summary>
    internal static bool PreferNoRuntimeZip()
    {
        try
        {
            var loc = typeof(object).Assembly.Location;
            if (string.IsNullOrWhiteSpace(loc))
                return false;

            // 典型：C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.x\System.Private.CoreLib.dll
            return loc.Contains(@"\dotnet\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase)
                   || loc.Contains("/dotnet/shared/Microsoft.NETCore.App/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 与 CI 产物一致：zip 名为 {主程序不含扩展名}-{version}-win-x64-*.zip；多 Flavor 并存时不能用仅后缀匹配。
    /// </summary>
    private static bool AssetMatchesCurrentProduct(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return false;
        var stem = Path.GetFileNameWithoutExtension(AppInfo.PrimaryExecutableFileName);
        if (string.IsNullOrEmpty(stem)) stem = "ClipboardX";

        if (!assetName.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase))
            return false;

        // 主程序名为 ClipboardX 时，必须排除子产品 zip（否则仅 「ClipboardX-」 会误匹配）
        if (string.Equals(stem, "ClipboardX", StringComparison.OrdinalIgnoreCase))
        {
            if (assetName.StartsWith("ClipboardX-clipboard-", StringComparison.OrdinalIgnoreCase))
                return false;
            if (assetName.StartsWith("ClipboardX-filejump-", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static ReleaseAssetInfo? PickZipAsset(List<GitHubAssetDoc>? assets, bool preferNoRuntime)
    {
        if (assets == null || assets.Count == 0) return null;

        ReleaseAssetInfo? first(string suffix, bool isNoRt)
        {
            foreach (var a in assets)
            {
                if (a.Name is not { Length: > 0 } n || string.IsNullOrEmpty(a.BrowserDownloadUrl)) continue;
                if (!n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!AssetMatchesCurrentProduct(n)) continue;
                return new ReleaseAssetInfo
                {
                    Name = n,
                    DownloadUrl = a.BrowserDownloadUrl,
                    Size = a.Size,
                    IsNoRuntimeVariant = isNoRt,
                };
            }

            return null;
        }

        if (preferNoRuntime)
        {
            return first(NoRuntimeSuffix, true)
                   ?? first(SelfContainedSuffix, false);
        }

        return first(SelfContainedSuffix, false)
               ?? first(NoRuntimeSuffix, true);
    }

    public static bool IsRemoteNewerThanCurrent(string remoteTag, string currentDisplayVersion)
    {
        var r = NormalizeVersionToken(remoteTag.TrimStart('v', 'V'));
        var c = NormalizeVersionToken(currentDisplayVersion.TrimStart('v', 'V'));
        if (!Version.TryParse(r, out var vr)) return false;
        if (!Version.TryParse(c, out var vc)) return false;
        return vr > vc;
    }

    private static string NormalizeVersionToken(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return "0.0.0.0";
        var parts = v.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0.0",
            2 => $"{parts[0]}.{parts[1]}.0.0",
            3 => $"{parts[0]}.{parts[1]}.{parts[2]}.0",
            _ => v,
        };
    }

    public static async Task DownloadToFileAsync(string downloadUrl, string filePath,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    public static void ExtractZipToDirectory(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
    }

    /// <summary>
    /// 写入 ps1 并启动 powershell；主进程应随后立即退出以释放 exe 锁。
    /// <paramref name="cleanupRoot"/> 为临时根目录（内含 extract 子目录等），脚本结束前会尝试删除。
    /// </summary>
    public static void LaunchDeferredReplaceAndRestart(string extractDir, string installDir, string cleanupRoot,
        string scriptPath, int currentPid = 0)
    {
        var extract = EscapeForPowerShellSingleQuoted(Path.GetFullPath(extractDir));
        var install = EscapeForPowerShellSingleQuoted(Path.GetFullPath(installDir));
        var root = EscapeForPowerShellSingleQuoted(Path.GetFullPath(cleanupRoot));
        var exe = EscapeForPowerShellSingleQuoted(
            Path.Combine(Path.GetFullPath(installDir), AppInfo.PrimaryExecutableFileName));

        var sb = new StringBuilder(512);
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        if (currentPid > 0)
        {
            sb.AppendLine($"$targetPid = {currentPid}");
            sb.AppendLine("$deadline = (Get-Date).AddSeconds(15)");
            sb.AppendLine("while ($true) {");
            sb.AppendLine("  $p = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
            sb.AppendLine("  if (!$p -or $p.HasExited) { break }");
            sb.AppendLine("  if ((Get-Date) -ge $deadline) {");
            sb.AppendLine("    Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("    Start-Sleep -Seconds 1");
            sb.AppendLine("    break");
            sb.AppendLine("  }");
            sb.AppendLine("  Start-Sleep -Milliseconds 250");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("Start-Sleep -Seconds 3");
        }
        sb.AppendLine($"$src = '{extract}'");
        sb.AppendLine($"$dst = '{install}'");
        sb.AppendLine($"$root = '{root}'");
        sb.AppendLine("Get-ChildItem -LiteralPath $src -Force | ForEach-Object {");
        sb.AppendLine("  $dest = Join-Path $dst $_.Name");
        sb.AppendLine("  Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force");
        sb.AppendLine("}");
        sb.AppendLine($"Start-Process -FilePath '{exe}'");
        sb.AppendLine("Start-Sleep -Seconds 1");
        sb.AppendLine("Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");
        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static string EscapeForPowerShellSingleQuoted(string s) =>
        s.Replace("'", "''", StringComparison.Ordinal);

    public static string FormatSizeMb(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }

    public static string TruncateNote(string? s, int max = 320)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>在 WinForms 对话框内显示跑马进度条并执行异步任务（用于下载）。</summary>
    public static Task RunWithMarqueeAsync(string waitText, Func<Task> work)
    {
        var tcs = new TaskCompletionSource();
        Exception? captured = null;
        using var f = new WinForms.Form
        {
            Text = "ClipboardX",
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            ControlBox = false,
            Width = 380,
            Height = 110,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
        };
        var lbl = new WinForms.Label
        {
            Text = waitText,
            Dock = WinForms.DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 12, 8, 4),
        };
        var pb = new WinForms.ProgressBar
        {
            Style = WinForms.ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 40,
            Dock = WinForms.DockStyle.Fill,
            Height = 22,
            Padding = new Padding(8, 4, 8, 12),
        };
        f.Controls.Add(lbl);
        f.Controls.Add(pb);

        f.FormClosed += (_, _) =>
        {
            if (captured != null)
                tcs.TrySetException(captured);
            else
                tcs.TrySetResult();
        };

        f.Shown += async (_, _) =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                if (f.IsHandleCreated && !f.IsDisposed)
                    f.BeginInvoke(() => f.Close());
            }
        };

        f.ShowDialog();
        return tcs.Task;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private sealed class GitHubReleaseDoc
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDoc>? Assets { get; set; }
    }

    private sealed class GitHubAssetDoc
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

}
