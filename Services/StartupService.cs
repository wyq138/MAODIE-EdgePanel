using System.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace EdgePanel.Services;

/// <summary>
/// 开机自启，三层兜底，保证拉得起来：
///   1. 系统级 StartupTask（需包标识，MSIX / 稀疏包场景；Run 键在包注册表虚拟化下不生效，必须走这条）；
///   2. HKCU\...\Run 注册表启动项（免安装默认路径）；
///   3. 计划任务（schtasks，登录时启动；前两条都写不进去时的最后兜底）。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgePanel";
    private const string StartupTaskId = "EdgePanelStartup";
    private const string TaskName = "EdgePanelAutostart";

    /// <summary>当前进程的可执行文件路径（自包含 exe）。</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>注册表中是否已存在本程序的自启项。</summary>
    public static bool IsEnabled()
    {
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null)
            {
                App.Log("打开 Run 注册表项失败（无写权限），改用计划任务兜底");
                return SetScheduledTask(enabled);
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
            return SetScheduledTask(enabled);
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
