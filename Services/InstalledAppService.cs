using System.Diagnostics;
using Windows.Management.Deployment;

namespace EdgePanel.Services;

/// <summary>本机扫描到的一个应用。</summary>
/// <param name="Name">显示名（来自快捷方式名 / 应用清单）。</param>
/// <param name="Target">启动目标：快捷方式路径，或 UWP 的 "uwp:AppUserModelId"。</param>
/// <param name="IsUwp">是否为 UWP / 打包应用（这类应用没有本地图标路径）。</param>
public sealed record InstalledApp(string Name, string Target, bool IsUwp)
{
    /// <summary>取图标用的路径；UWP 应用没有可直接解析的路径，返回 null 由界面用占位图标。</summary>
    public string? IconSource => IsUwp ? null : Target;
}

/// <summary>
/// 扫描本机已安装应用：开始菜单里的 .lnk（涵盖绝大多数桌面程序）+ 打包应用（UWP / Store）。
/// 结果缓存在内存里，界面刷新时才重新扫描。
/// </summary>
public static class InstalledAppService
{
    private static readonly List<InstalledApp> Cache = new();
    private static bool _loaded;

    public static IReadOnlyList<InstalledApp> Apps => Cache;

    /// <summary>扫描（或直接返回缓存）。<paramref name="refresh"/> 为 true 时强制重扫。</summary>
    public static async Task<IReadOnlyList<InstalledApp>> LoadAsync(bool refresh = false)
    {
        if (_loaded && !refresh)
        {
            return Cache;
        }

        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() => ScanShortcuts(list, seen));
        await ScanPackagedAppsAsync(list, seen);

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        Cache.Clear();
        Cache.AddRange(list);
        _loaded = true;
        return Cache;
    }

    public static InstalledApp? Find(string target)
        => Cache.FirstOrDefault(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>启动一个应用；找不到定义时按普通路径打开。</summary>
    public static void Launch(string target)
    {
        if (target.StartsWith("uwp:", StringComparison.OrdinalIgnoreCase))
        {
            LaunchPackaged(target[4..]);
            return;
        }

        SystemActions.OpenPath(target);
    }

    // ————————————————————————— 开始菜单快捷方式 —————————————————————————

    private static void ScanShortcuts(List<InstalledApp> list, HashSet<string> seen)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateShortcuts(root))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || IsNoise(name) || !seen.Add(name))
                {
                    continue;
                }

                list.Add(new InstalledApp(name, file, false));
            }
        }
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > 3)
            {
                continue;
            }

            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk");
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                // 个别目录没权限，跳过即可
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in subDirs)
            {
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    /// <summary>开始菜单里混着卸载程序、说明文档、官网链接，这些不算"应用"。</summary>
    private static bool IsNoise(string name)
    {
        string[] keywords =
        {
            "uninstall", "卸载", "readme", "read me", "help", "帮助", "说明",
            "website", "homepage", "官网", "文档", "documentation", "license", "许可",
            "update", "更新", "设置向导", "setup", "网址",
        };

        return keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // ————————————————————————— 打包应用（UWP / Store） —————————————————————————

    private static async Task ScanPackagedAppsAsync(List<InstalledApp> list, HashSet<string> seen)
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage)
                    {
                        continue;
                    }

                    var entries = await package.GetAppListEntriesAsync();
                    foreach (var entry in entries)
                    {
                        var aumid = entry.AppUserModelId;
                        if (string.IsNullOrWhiteSpace(aumid))
                        {
                            continue;
                        }

                        var name = entry.DisplayInfo?.DisplayName;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = package.DisplayName;
                        }

                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        {
                            continue;
                        }

                        list.Add(new InstalledApp(name, "uwp:" + aumid, true));
                    }
                }
                catch
                {
                    // 个别包信息读不到（权限 / 已损坏），忽略
                }
            }
        }
        catch (Exception ex)
        {
            // 免安装进程读不到包信息也不影响：开始菜单那部分已经够用了
            App.Log("扫描打包应用失败：" + ex.Message);
        }
    }

    private static void LaunchPackaged(string aumid)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + aumid,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Log($"启动打包应用失败 {aumid}：{ex.Message}");
        }
    }
}
