using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EdgePanel.Services;

/// <summary>常用系统操作集合（锁屏 / 睡眠 / 关机 / 静音 / 清空回收站…）。</summary>
public static class SystemActions
{
    public static void LockScreen() => Start("rundll32.exe", "user32.dll,LockWorkStation");

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Shutdown() => Start("shutdown.exe", "/s /t 0");

    public static void Restart() => Start("shutdown.exe", "/r /t 0");

    public static void ToggleMute()
    {
        keybd_event(VK_VOLUME_MUTE, 0, 0, 0);
        keybd_event(VK_VOLUME_MUTE, 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>系统提示音（用于计时器结束提醒）。</summary>
    public static void Beep() => MessageBeep(0xFFFFFFFF);

    public static void OpenTaskManager() => Start("taskmgr.exe", "");

    public static void OpenSettings() => Start("ms-settings:", "");

    public static void OpenExplorer() => Start("explorer.exe", "");

    public static void OpenControlPanel() => Start("control.exe", "");

    public static void OpenResourceMonitor() => Start("resmon.exe", "");

    public static void ClearRecycleBin() => _ = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    public static void EmptyTempFolder()
    {
        try
        {
            var temp = Path.GetTempPath();
            foreach (var file in Directory.EnumerateFiles(temp))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 被占用的文件跳过
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("清理临时目录失败：" + ex.Message);
        }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log($"打开失败 {path}：{ex.Message}");
        }
    }

    public static void RunCommand(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            App.Log($"执行命令失败 {command}：{ex.Message}");
        }
    }

    private static void Start(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log($"启动失败 {file}：{ex.Message}");
        }
    }

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}