using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ToastBridge;

/// <summary>
/// 原生消息桥的导出面。
///
/// 主程序（免安装 WinUI）通过 LoadLibrary 加载本 DLL，拿到下面的函数指针后调用：
///   int ToastBridge_Start(void (__cdecl* callback)(const wchar_t* payload));
///   int ToastBridge_Stop();
///
/// payload 是一段以 '\0' 结尾的 UTF-16 字符串，三个字段用 U+001F（单元分隔符）隔开：
///   app U+001F title U+001F body
/// 回调后指针即失效，调用方必须在返回前把内容拷走。
/// 返回值是 HRESULT：0 = S_OK，其它为失败的 HRESULT。
///
/// 这条通道直接调用 WinRT 的 UserNotificationListener，订阅 NotificationChanged；
/// 事件一到就把该通知的标题/正文取出来推给主程序，延迟是实时的（毫秒级），
/// 不是轮询。若订阅失败（免安装进程没有包标识时系统会返回 ERROR_NOT_FOUND），
/// 返回对应 HRESULT，由主程序回退到轮询。
/// </summary>
public static class Exports
{
    private static IntPtr _callback;
    private static UserNotificationListener? _listener;
    private static TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? _handler;

    /// <summary>没有包标识时兜底用的显式 AUMID（有包标识时用包自己的，不能再改）。</summary>
    private const string FallbackAumId = "EdgePanel.Sidebar";

    /// <summary>进程是否有包标识（有的话系统会给它 AUMID，通知平台才认它是登记过的订阅方）。</summary>
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId);

    private static bool HasPackageIdentity()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) == ErrorInsufficientBuffer;
    }

    /// <summary>订阅系统通知变化，实时推送新通知。返回 HRESULT。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ToastBridge_Start")]
    public static int Start(IntPtr callback)
    {
        try
        {
            Unsubscribe();

            _callback = callback;

            // 有包标识（稀疏包注册成功）时系统已经给了 AUMID，直接用；
            // 没有就退而求其次，抢在创建 listener 之前设一个显式 AUMID——
            // 上一版失败的原因之一就是先建了 listener 实例、后设 AUMID，那时进程身份早就定死了。
            if (HasPackageIdentity())
            {
                Log("Start: 进程已有包标识");
            }
            else
            {
                var aumidResult = SetCurrentProcessExplicitAppUserModelID(FallbackAumId);
                Log($"Start: 无包标识，设显式 AUMID -> 0x{aumidResult:X8}");
            }

            var listener = UserNotificationListener.Current;
            _listener = listener;

            _handler = OnNotificationChanged;
            listener.NotificationChanged += _handler;

            Log("Start: 已订阅 NotificationChanged（实时推送）");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Start 失败：{Describe(ex)}");
            Unsubscribe();
            return ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
        }
    }

    /// <summary>取消订阅。返回 HRESULT。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ToastBridge_Stop")]
    public static int Stop()
    {
        try
        {
            Unsubscribe();
            return 0;
        }
        catch (Exception ex)
        {
            Log("Stop 失败：" + Describe(ex));
            return ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
        }
    }

    /// <summary>真正的退订逻辑（导出方法带 UnmanagedCallersOnly，不能互相直接调用）。</summary>
    private static void Unsubscribe()
    {
        if (_listener is not null && _handler is not null)
        {
            _listener.NotificationChanged -= _handler;
        }

        _handler = null;
        _listener = null;
        _callback = IntPtr.Zero;
    }

    private static void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        try
        {
            if (args.ChangeKind != UserNotificationChangedKind.Added)
            {
                return;
            }

            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null)
            {
                return;
            }

            // ToastGeneric 绑定的文本元素按顺序是：标题、正文（正文可能多段）。
            var texts = notification.Notification
                ?.Visual
                ?.GetBinding(KnownNotificationBindings.ToastGeneric)
                ?.GetTextElements()?
                .Select(element => element.Text?.Trim())
                .Where(text => !string.IsNullOrEmpty(text))
                .Select(text => text!)
                .ToList() ?? new List<string>();

            var app = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var title = texts.Count > 0 ? texts[0] : "";
            var body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "";

            Invoke(app + "\u001F" + title + "\u001F" + body);
        }
        catch (Exception ex)
        {
            Log("处理通知失败：" + Describe(ex));
        }
    }

    /// <summary>把 payload 交给主程序注册的回调（同一线程内同步调用，返回即失效）。</summary>
    private static unsafe void Invoke(string payload)
    {
        var callback = _callback;
        if (callback == IntPtr.Zero)
        {
            return;
        }

        var fn = (delegate* unmanaged[Cdecl]<IntPtr, void>)callback;
        fixed (char* buffer = payload)
        {
            fn((IntPtr)buffer);
        }
    }

    private static string Describe(Exception ex)
        => $"{ex.GetType().FullName} (0x{ex.HResult:X8}) {ex.Message}"
           + (ex.InnerException is null ? "" : " <- " + Describe(ex.InnerException));

    /// <summary>诊断日志：%LOCALAPPDATA%\EdgePanel\logs\toastbridge.log（独立文件，不与主程序抢写）。</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgePanel", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "toastbridge.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}