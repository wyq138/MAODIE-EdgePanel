using System.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace EdgePanel.Services;

/// <summary>
/// 开机自启，按运行环境分流：
///   - 有包标识（商店 MSIX / 稀疏包）：走系统 StartupTask（清单里声明 windows.startupTask 扩展）。
///     Run 注册表键在包注册表虚拟化下会被重定向到包私有配置单元，写了也不生效，必须走这条；
///   - 无包标识（免安装默认路径）：HKCU\...\Run 注册表启动项；
///   - 计划任务（schtasks，登录时启动）：无包标识且 Run 键写不进去时的最后兜底。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgePanel";
    private const string StartupTaskId = "EdgePanelStartup";
    private const string TaskName = "EdgePanelAutostart";

    /// <summary>当前进程的可执行文件路径（自包含 exe）。</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? "";

    private static bool UseStartupTask => PackageIdentity.HasIdentity;

    /// <summary>注册表中是否已存在本程序的自启项。</summary>
    public static bool IsEnabled()
    {
        if (UseStartupTask)
        {
            return GetStartupTask()?.State is StartupTaskState.Enabled
                or StartupTaskState.EnabledByPolicy;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value))
            {
                return ScheduledTaskExists();
            }

            var exe = ExecutablePath;
            return string.IsNullOrEmpty(exe) ||
                   value.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.Log("读取开机自启状态失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>启用 / 停用开机自启；返回是否成功。</summary>
    public static bool SetEnabled(bool enabled)
    {
        if (UseStartupTask)
        {
            return SetStartupTask(enabled);
        }

        return SetRunKey(enabled) || SetScheduledTask(enabled);
    }

    // ————————————————————————— StartupTask（MSIX / 稀疏包） —————————————————————————

    private static StartupTask? GetStartupTask()
    {
        try
        {
            // WinRT 异步的回调会投递回调用线程的上下文，直接在 UI 线程上
            // GetResult() 会死锁，必须放到线程池里等
            var tasks = Task.Run(() =>
                StartupTask.GetForCurrentPackageAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            foreach (var task in tasks)
            {
                if (task.TaskId == StartupTaskId)
                {
                    return task;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("获取 StartupTask 失败：" + ex.Message);
        }
        return null;
    }

    private static bool SetStartupTask(bool enabled)
    {
        var task = GetStartupTask();
        if (task is null)
        {
            App.Log("StartupTask 不存在（清单缺少 windows.startupTask 扩展？），改用计划任务兜底");
            return SetScheduledTask(enabled);
        }

        try
        {
            if (enabled)
            {
                var state = Task.Run(() =>
                    task.RequestEnableAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
                var ok = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                App.Log(ok
                    ? "已启用系统 StartupTask 开机自启"
                    : $"StartupTask 请求启用被拒：{state}（用户可能在系统设置里关闭了自启）");
                return ok;
            }

            task.Disable();
            App.Log("已禁用系统 StartupTask 开机自启");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("设置 StartupTask 失败：" + ex.Message);
            return false;
        }
    }

    // ————————————————————————— Run 注册表键（免安装） —————————————————————————

    private static bool SetRunKey(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null)
            {
                App.Log("打开 Run 注册表项失败（无写权限），改用计划任务兜底");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                SetScheduledTask(false);
                App.Log("已关闭开机自启");
                return true;
            }

            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe))
            {
                App.Log("无法确定可执行文件路径，开机自启未设置");
                return false;
            }

            key.SetValue(ValueName, $"\"{exe}\"");
            SetScheduledTask(false); // Run 键成功后清理可能残留的计划任务，避免双重启动
            App.Log($"已设置开机自启：{exe}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("设置开机自启失败：" + ex.Message);
            return false;
        }
    }

    // ————————————————————————— 计划任务兜底 —————————————————————————

    private static bool SetScheduledTask(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                App.Log("已移除自启计划任务（如存在）");
                return true;
            }

            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe))
            {
                App.Log("无法确定可执行文件路径，计划任务未创建");
                return false;
            }

            var args = $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL LIMITED "
                       + $"/TR \"\\\"{exe}\\\"\"";
            var ok = RunSchtasks(args);
            App.Log(ok ? "已通过计划任务设置开机自启" : "计划任务创建失败");
            return ok;
        }
        catch (Exception ex)
        {
            App.Log("计划任务设置异常：" + ex.Message);
            return false;
        }
    }

    private static bool ScheduledTaskExists()
    {
        return RunSchtasks($"/Query /TN \"{TaskName}\"");
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
