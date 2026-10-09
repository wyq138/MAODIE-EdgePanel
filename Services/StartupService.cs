using Microsoft.Win32;

namespace EdgePanel.Services;

/// <summary>
/// 开机自启：写入 / 移除 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 免安装（非 MSIX）模式下用注册表启动项，无需额外计划任务或服务。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgePanel";

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
                return false;
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
                App.Log("打开 Run 注册表项失败（无写权限）");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
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
            App.Log($"已设置开机自启：{exe}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("设置开机自启失败：" + ex.Message);
            return false;
        }
    }
}