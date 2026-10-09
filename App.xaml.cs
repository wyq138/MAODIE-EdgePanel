using System.Diagnostics;
using System.Text;
using EdgePanel.Core;
using Microsoft.UI.Xaml;

namespace EdgePanel;

public partial class App : Application
{
    /// <summary>全局日志目录：%LOCALAPPDATA%\EdgePanel\logs（避开 C 盘项目存储压力）</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgePanel");

    public static string LogDir { get; } = Path.Combine(DataRoot, "logs");

    /// <summary>唯一的贴边面板窗口（可在左/右边缘之间拖动切换）。</summary>
    public static PanelWindow? Panel { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        Directory.CreateDirectory(LogDir);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("应用启动");
        // 没有包标识就先注册稀疏包（下次启动生效），消息实时推送依赖它
        Services.PackageIdentity.EnsureRegistered();

        var settings = SettingsStore.Load();

        // 剪贴板历史需要在启动时就监听，否则打开页面之前的复制内容会丢失
        Services.ClipboardService.Instance.Start();

        if (settings.PanelEnabled)
        {
            Panel = new PanelWindow(settings);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log("未处理异常: " + e.Exception);
    }

    /// <summary>运行时开启/关闭贴边面板（设置页调用）。</summary>
    public static void SetPanelEnabled(bool enabled)
    {
        try
        {
            if (enabled && Panel is null)
            {
                Panel = new PanelWindow(SettingsStore.Current);
            }
            else if (!enabled && Panel is not null)
            {
                Panel.Close();
                Panel = null;
            }
        }
        catch (Exception ex)
        {
            Log("切换面板失败：" + ex.Message);
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(LogDir, "edgepanel.log"), line, Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}