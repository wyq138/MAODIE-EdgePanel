using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EdgePanel.Services;

/// <summary>
/// 给应用"上户口"。免安装进程天生没有包标识，Windows 通知平台会拒绝
/// UserNotificationListener.NotificationChanged 的实时订阅（0x80070490）。
///
/// 这里在启动时把随包的身份清单（AppxManifest.xml）注册成稀疏包（带外部位置的身份包），
/// 进程带上包标识 + userNotificationListener 能力之后，实时订阅才被系统放行。
/// 身份在进程启动那一刻就定死了，所以首次注册要等下次启动才生效；
/// 注册失败也不影响使用——消息监听会自动退化为轮询。
/// </summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    /// <summary>当前进程是否已经带包标识。</summary>
    public static bool HasIdentity
    {
        get
        {
            try
            {
                var length = 0;
                return GetCurrentPackageFullName(ref length, null) == ErrorInsufficientBuffer;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>启动时调用：没有包标识就尝试注册（首次注册要到下次启动才生效）。</summary>
    public static void EnsureRegistered()
    {
        if (HasIdentity)
        {
            App.Log("包标识：已具备");
            return;
        }

        _ = Task.Run(Register);
    }

    private static void Register()
    {
        try
        {
            // 身份包只放清单和图标（Package\），真正的应用文件用 -ExternalLocation 指到 exe 所在目录。
            // 这样 AppX 不会去锁应用目录，exe 照常双击运行，同时又拿到了包标识。
            var appDir = AppContext.BaseDirectory.TrimEnd('\\');
            var manifest = Path.Combine(appDir, "Package", "AppxManifest.xml");
            if (!File.Exists(manifest))
            {
                App.Log("包标识：身份清单不存在，跳过注册：" + manifest);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                            + "\"$ErrorActionPreference='SilentlyContinue'; "
                            + "Get-AppxPackage 7B6AFBFB.42750BAC206B9 | Remove-AppxPackage; "
                            + $"Add-AppxPackage -Register '{manifest}' -ExternalLocation '{appDir}'\"",
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                App.Log("包标识：无法启动注册进程");
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                App.Log("包标识：注册成功，下次启动生效（实时消息推送将走系统通知事件）");
            }
            else
            {
                App.Log($"包标识：注册失败（退出码 {process.ExitCode}）：{error.Trim()} {output.Trim()}；"
                        + "消息监听将继续使用轮询兜底");
            }
        }
        catch (Exception ex)
        {
            App.Log("包标识：注册异常：" + ex.Message);
        }
    }
}