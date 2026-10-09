using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace EdgePanel.Services;

/// <summary>截获到的一条系统弹窗消息。</summary>
public sealed class NotificationMessage
{
    /// <summary>发消息的应用（QQ / 微信 / 邮件…），取不到时为空。</summary>
    public string App { get; init; } = "";

    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>岛上显示的来源标签。</summary>
    public string Label => string.IsNullOrWhiteSpace(App) ? "新消息" : App;
}

/// <summary>
/// 系统弹窗消息监听：QQ、微信、邮件等一切走 Windows 通知弹窗的消息都会被截到。
///
/// 三条通道，按优先级往下退：
///   1. 原生消息桥（首选）：LoadLibrary 加载自己用 Native AOT 编的 ToastBridge.dll，
///      由它订阅 UserNotificationListener.NotificationChanged，通知一到就实时推回来，
///      延迟是毫秒级，正文/标题都是通知里的原始字符串，不用截屏 OCR。
///   2. 托管订阅：系统事件直接在本进程订阅（需要包标识，见 Package\AppxManifest.xml）。
///   3. 1 秒轮询：前面都用不了时的兜底（免安装、没注册包标识时就是这条路），
///      同样只看"这个 Id 之前没见过"的通知，延迟 1 秒级。
/// </summary>
public sealed class NotificationWatcher
{
    /// <summary>退化为轮询时的间隔：1 秒问一次，弹窗基本是"刚冒出来就出岛"。</summary>
    private const int PollIntervalMs = 1000;

    /// <summary>原生桥的 payload 里，app / title / body 三个字段的分隔符（U+001F 单元分隔符）。</summary>
    private const char FieldSeparator = '\u001F';

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
    private readonly HashSet<uint> _seen = new();

    private DispatcherQueue? _dispatcher;
    private UserNotificationListener? _listener;
    private TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? _managedHandler;
    private IntPtr _bridgeModule;
    private bool _started;
    private bool _pollErrorLogged;

    public static NotificationWatcher Instance { get; } = new();

    /// <summary>截到一条消息（在 UI 线程上触发）。</summary>
    public event Action<NotificationMessage>? Received;

    private NotificationWatcher()
    {
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
            _listener = listener;
        }
        catch (Exception ex)
        {
            App.Log("取通知监听器失败：" + Describe(ex));
            return;
        }

        UserNotificationListenerAccessStatus access;
        try
        {
            // 首次会弹系统授权框，用户允许后才有权限读通知；被拒绝就只能去
            // 设置 → 隐私和安全性 → 通知 里重新打开。
            access = await listener.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            App.Log("申请通知读取权限失败：" + Describe(ex));
            return;
        }

        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            App.Log($"消息监听未获授权（{access}），弹窗消息无法截取");
            return;
        }

        // 首选：原生消息桥实时推送
        if (TryStartBridge())
        {
            App.Log("消息监听已启动（原生桥接，实时推送）");
            return;
        }

        // 次选：本进程直接订阅系统通知事件
        try
        {
            _managedHandler = OnNotificationChanged;
            listener.NotificationChanged += _managedHandler;
            App.Log("消息监听已启动（托管订阅系统通知变化）");
            return;
        }
        catch (Exception ex)
        {
            App.Log($"托管订阅通知事件失败，改用轮询：{Describe(ex)}");
        }

        // 兜底：轮询
        StartPolling(listener);
    }

    // ————————————————————————— 原生消息桥 —————————————————————————

    /// <summary>加载 ToastBridge.dll 并订阅；成功返回 true。失败原因写日志。</summary>
    private unsafe bool TryStartBridge()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ToastBridge.dll");
            if (!File.Exists(path))
            {
                App.Log("原生消息桥不存在：" + path);
                return false;
            }

            if (!NativeLibrary.TryLoad(path, out _bridgeModule))
            {
                App.Log("加载原生消息桥失败：" + path);
                return false;
            }

            if (!NativeLibrary.TryGetExport(_bridgeModule, "ToastBridge_Start", out var start))
            {
                App.Log("原生消息桥缺少导出 ToastBridge_Start");
                return false;
            }

            var fn = (delegate* unmanaged[Cdecl]<IntPtr, int>)start;
            var result = fn((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnBridgeMessage);
            if (result == 0)
            {
                return true;
            }

            App.Log($"原生消息桥订阅失败：HRESULT 0x{result:X8}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log("启动原生消息桥异常：" + Describe(ex));
            return false;
        }
    }

    /// <summary>原生桥推过来的一条消息（payload = app U+001F title U+001F body）。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnBridgeMessage(IntPtr payload)
    {
        try
        {
            var text = Marshal.PtrToStringUni(payload);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var parts = text.Split(FieldSeparator);
            Instance.Publish(new NotificationMessage
            {
                App = parts.Length > 0 ? parts[0] : "",
                Title = parts.Length > 1 ? parts[1] : "",
                Body = parts.Length > 2 ? parts[2] : "",
            });
        }
        catch (Exception ex)
        {
            App.Log("处理原生桥消息失败：" + Describe(ex));
        }
    }

    // ————————————————————————— 兜底轮询 —————————————————————————

    private void StartPolling(UserNotificationListener listener)
    {
        // 启动时先把已存在的历史通知记下来，避免一开面板就把旧消息全播一遍
        _ = SeedAsync(listener);

        _pollTimer.Tick += (_, _) => _ = PollAsync(listener);
        _pollTimer.Start();
    }

    private async Task SeedAsync(UserNotificationListener listener)
    {
        try
        {
            var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var item in existing)
            {
                _seen.Add(item.Id);
            }

            App.Log($"消息监听已启动（轮询，{_seen.Count} 条历史通知不再重播）");
        }
        catch (Exception ex)
        {
            App.Log("读取通知列表失败：" + Describe(ex));
        }
    }

    private async Task PollAsync(UserNotificationListener listener)
    {
        try
        {
            var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var item in current)
            {
                // 见过的 Id 不再播报，所以同一条通知在整个生命周期里只出岛一次
                if (_seen.Add(item.Id))
                {
                    Publish(ToMessage(item));
                }
            }
        }
        catch (Exception ex)
        {
            if (_pollErrorLogged)
            {
                return;
            }

            _pollErrorLogged = true;
            App.Log("轮询通知失败：" + Describe(ex));
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        try
        {
            // 只看新来的通知；撤下（Removed）不管
            if (args.ChangeKind != UserNotificationChangedKind.Added)
            {
                return;
            }

            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null)
            {
                return;
            }

            Publish(ToMessage(notification));
        }
        catch (Exception ex)
        {
            App.Log("读取通知内容失败：" + ex.Message);
        }
    }

    private static NotificationMessage ToMessage(UserNotification notification)
    {
        // ToastGeneric 绑定的文本元素按顺序就是：标题、正文（正文可能有多段）。
        // 注意这层投影里 UserNotification 自己没有 Visual，正文挂在它的 Notification 上。
        var texts = notification.Notification
            ?.Visual
            ?.GetBinding(KnownNotificationBindings.ToastGeneric)
            ?.GetTextElements()?
            .Select(element => element.Text?.Trim())
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToList() ?? new List<string>();

        return new NotificationMessage
        {
            App = notification.AppInfo?.DisplayInfo?.DisplayName ?? "",
            Title = texts.Count > 0 ? texts[0] : "",
            Body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "",
        };
    }

    /// <summary>异常的完整身份：类型 + HRESULT + 消息 + 内层，便于定位失败在哪一步。</summary>
    private static string Describe(Exception ex)
        => $"{ex.GetType().FullName} (0x{ex.HResult:X8}) {ex.Message}"
           + (ex.InnerException is null ? "" : " <- " + Describe(ex.InnerException));

    private void Publish(NotificationMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Body))
        {
            return;
        }

        App.Log($"截到消息：{message.Label} / {message.Title} / {message.Body}");

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() => Received?.Invoke(message));
    }
}