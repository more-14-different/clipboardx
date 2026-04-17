using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClipboardManager;

/// <summary>
/// 通过 Everything 随附的 Everything64.dll 做同步 IPC 查询（需本机已运行 Everything）。
/// DLL 从 exe 目录（发布时由 native\Everything64.dll 复制到输出根）、native 子目录、PATH、注册表等路径探测加载。
/// </summary>
internal static class EverythingIpc
{
    private static readonly object IpcSync = new();

    private const uint EverythingRequestFullPathAndFileName = 0x00000004;
    private const uint EverythingErrorOk = 0;
    private const int SearchFragmentCapacity = 2048;

    /// <summary>未在任何候选路径找到 DLL（与 Everything 进程是否运行无关）。</summary>
    public const int LastErrorDllNotFound = -100;

    /// <summary>P/Invoke 调用异常（位宽/入口不匹配等）。</summary>
    public const int LastErrorInterop = -101;

    static EverythingIpc()
    {
        NativeLibrary.SetDllImportResolver(typeof(EverythingIpc).Assembly, ResolveEverything64Module);
    }

    private static IntPtr ResolveEverything64Module(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "Everything64.dll", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var dir in CandidateProbeDirectories())
        {
            var p = Path.Combine(dir, Environment.Is64BitProcess ? "Everything64.dll" : "Everything32.dll");
            try
            {
                if (File.Exists(p))
                    return NativeLibrary.Load(p);
            }
            catch
            {
                /* try next */
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateProbeDirectories()
    {
        var loc = typeof(EverythingIpc).Assembly.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            var d = Path.GetDirectoryName(loc);
            if (!string.IsNullOrEmpty(d))
            {
                var root = d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                yield return root;
                yield return Path.Combine(root, "native");
            }
        }

        var b = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(b))
        {
            var root = b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            yield return root;
            yield return Path.Combine(root, "native");
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "Everything");

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86))
            yield return Path.Combine(pf86, "Everything");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Programs", "Everything");

        foreach (var d in ProbePathEnvironment())
            yield return d;

        foreach (var d in ProbeRegistryEverythingInstallDirs())
            yield return d;
    }

    private static IEnumerable<string> ProbePathEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var seg in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = seg.Trim().Trim('"');
            if (t.Length > 0 && Directory.Exists(t))
                yield return t;
        }
    }

    private static IEnumerable<string> ProbeRegistryEverythingInstallDirs()
    {
        foreach (var d in CollectRegistryEverythingInstallDirs())
            yield return d;
    }

    private static List<string> CollectRegistryEverythingInstallDirs()
    {
        var list = new List<string>(4);
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in new[]
                     {
                         @"SOFTWARE\voidtools\Everything",
                         @"SOFTWARE\WOW6432Node\voidtools\Everything",
                     })
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k == null) continue;
                    foreach (var name in new[] { "exe_path", "install_path", "path" })
                    {
                        var v = k.GetValue(name) as string;
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        v = v.Trim().Trim('"');
                        string? dir = null;
                        if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            dir = Path.GetDirectoryName(v);
                        else if (Directory.Exists(v))
                            dir = v;

                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            list.Add(dir);
                            break;
                        }
                    }
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        return list;
    }

    /// <summary>同一进程内 Everything IPC 为全局状态，与其它查询串行执行（含资源管理器双次查询、跳转列表文件夹检索）。</summary>
    public static void InvokeExclusive(Action action)
    {
        lock (IpcSync) action();
    }

    /// <summary>尝试查询；失败时 <paramref name="lastError"/> 为 Everything_GetLastError，DLL 未加载为 <see cref="LastErrorDllNotFound"/> 等。</summary>
    public static bool TryQueryFullPaths(string searchExpression, int maxResults, List<string> paths, out int lastError)
    {
        lock (IpcSync)
            return TryQueryFullPathsCore(searchExpression, maxResults, paths, out lastError);
    }

    /// <summary>限定为文件夹的检索（<c>folder:"…"</c>），并剔除已不存在的路径。</summary>
    public static bool TryQueryFolderPaths(string userTyping, int maxResults, List<string> paths, out int lastError)
    {
        paths.Clear();
        lastError = LastErrorDllNotFound;
        var q = userTyping.Trim();
        if (q.Length == 0)
        {
            lastError = unchecked((int)EverythingErrorOk);
            return true;
        }

        if (q.Length > 1800) q = q[..1800];
        var escaped = q.Replace("\"", "\\\"");
        var search = $"folder:\"{escaped}\"";

        lock (IpcSync)
        {
            if (!TryQueryFullPathsCore(search, maxResults, paths, out lastError))
                return false;

            for (var i = paths.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!Directory.Exists(paths[i]))
                        paths.RemoveAt(i);
                }
                catch
                {
                    paths.RemoveAt(i);
                }
            }

            lastError = unchecked((int)EverythingErrorOk);
            return true;
        }
    }

    /// <summary>无锁；须在 <see cref="InvokeExclusive"/> 或 <see cref="TryQueryFullPaths"/> 已持锁时调用。</summary>
    internal static bool TryQueryFullPathsCore(string searchExpression, int maxResults, List<string> paths, out int lastError)
    {
        paths.Clear();
        lastError = LastErrorDllNotFound;

        try
        {
            Everything_Reset();
        }
        catch (DllNotFoundException)
        {
            lastError = LastErrorDllNotFound;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            lastError = LastErrorInterop;
            return false;
        }

        try
        {
            // 不在此处依赖 Everything_IsDBLoaded：部分版本/安装下 IPC 已可查询但该标志仍长期为 0，会误报「未运行」。

            Everything_SetRequestFlags(EverythingRequestFullPathAndFileName);
            Everything_SetMax((uint)Math.Clamp(maxResults, 1, 10_000));
            // 搜索串由 ExplorerQuickFind 使用 parent:"路径" 构造；勿依赖 SetMatchPath，亦勿再使用「C:\ 关键词」形式（IPC 实测恒 0 条）

            var buf = searchExpression.AsSpan();
            if (buf.Length > SearchFragmentCapacity)
                buf = buf.Slice(0, SearchFragmentCapacity);
            Everything_SetSearchW(buf.ToString());

            if (Everything_QueryW(1) == 0)
            {
                lastError = unchecked((int)Everything_GetLastError());
                return false;
            }

            var n = Everything_GetNumResults();
            var cap = Math.Min((uint)n, (uint)maxResults);
            for (uint i = 0; i < cap; i++)
            {
                var sb = new StringBuilder(32768);
                Everything_GetResultFullPathNameW(i, sb, sb.Capacity);
                var s = sb.ToString().Trim();
                if (s.Length > 0)
                    paths.Add(s);
            }

            lastError = unchecked((int)EverythingErrorOk);
            return true;
        }
        catch (Exception)
        {
            lastError = LastErrorInterop;
            return false;
        }
    }

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_Reset();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, EntryPoint = "Everything_SetSearchW")]
    private static extern void Everything_SetSearchW(string lpSearchString);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetRequestFlags(uint dwRequestFlags);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetMax(uint dwMax);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern int Everything_QueryW(int bWait);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetLastError();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, int nMaxCount);
}
