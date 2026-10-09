using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using EdgePanel.Core;
using Microsoft.UI.Dispatching;
using RapidOcrNet;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EdgePanel.Services;

/// <summary>
/// 自绘弹窗通道：QQ、微信这类"不往通知中心登记、自己画一个窗口弹出来"的消息，
/// 走 UserNotificationListener 是读不到的（它只认注册进通知中心的通知）。
/// 这里换一条路：用 WinEvent 钩子盯住"新的置顶小窗口出现"，把那个窗口的像素抓下来 OCR，
/// 读出文字再交给同一套消息岛。两条通道互补——
/// NotificationWatcher 管注册到通知中心的，本类管自绘窗口的。
/// </summary>
public sealed class PopupWatcher
{
    public static PopupWatcher Instance { get; } = new();

    /// <summary>截到一条自绘弹窗（在 UI 线程上触发）。</summary>
    public event Action<NotificationMessage>? Detected;

    // WinEvent：只订阅"窗口显示出来"这一种事件
    private const uint EventObjectShow = 0x8002;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;
    private const uint WineventOutOfContext = 0x0000;

    // 弹窗的尺寸范围：太小的多半是图标/提示点，太大的多半是应用主窗口或对话框
    private const int MinWidth = 120;
    private const int MaxWidth = 800;
    private const int MinHeight = 50;
    private const int MaxHeight = 600;

    /// <summary>等窗口把字画出来再抓屏，太早抓到的是空白。</summary>
    private const int PaintWaitMs = 260;

    /// <summary>候选窗口的诊断日志条数上限，避免长时间运行把日志刷爆。</summary>
    private const int MaxDiagnostics = 40;

    /// <summary>同一个窗口短时间内只处理一次（保留 30 秒，兼顾句柄被回收复用的情况）。</summary>
    private static readonly TimeSpan HandleCooldown = TimeSpan.FromSeconds(30);

    /// <summary>文字完全相同的两条弹窗在 5 秒内只出岛一次。</summary>
    private static readonly TimeSpan TextCooldown = TimeSpan.FromSeconds(5);

    /// <summary>壳/系统窗口，长得再像弹窗也不是消息。</summary>
    private static readonly string[] IgnoredClasses =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "tooltips_class32",
        "SysShadow", "#32768", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "Progman", "WorkerW", "MultitaskingViewFrame",
        "ForegroundStubWindow", "EdgeUiInputTopWndClass", "Windows.Internal.Shell.TabProxyWindow",
        "OleMainThreadWndClass", "TaskListThumbnailWnd", "ConsoleWindowClass", "PseudoConsoleWindow",
        "IME", "Default IME", "MSCTFIME UI", "GDI+ Hook Window Class",
    };

    /// <summary>明显不是聊天软件的噪声进程。</summary>
    private static readonly string[] IgnoredProcesses =
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "TextInputHost", "ShellExperienceHost",
        "ApplicationFrameHost", "SystemSettings", "dwm", "winlogon", "LockApp", "ctfmon",
        "RuntimeBroker", "sihost", "taskhostw", "Widgets", "WidgetService", "EdgePanel",
        // 浏览器的悬浮面板（右键菜单、地址栏下拉、查找条、翻译条…）又小又置顶，
        // 很容易被当成弹窗消息；浏览器真正的通知走系统通知中心那条通道，这里直接跳过。
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "msedgewebview2", "WebViewHost",
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<IntPtr, DateTime> _handled = new();
    private readonly Dictionary<string, DateTime> _recentTexts = new();
    private readonly object _mapGate = new();

    private DispatcherQueue? _dispatcher;
    private Thread? _thread;
    private WinEventProc? _callback;
    private IntPtr _hook;
    private bool _started;
    private int _diagnostics;
    private RapidOcr? _offline;
    private bool _offlineTried;

    private PopupWatcher()
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

        _thread = new Thread(HookLoop) { IsBackground = true, Name = "EdgePanel 弹窗监听" };
        _thread.Start();
    }

    /// <summary>独立的钩子线程：装钩子后一直跑消息循环，系统才会把事件回调递过来。</summary>
    private void HookLoop()
    {
        // 回调委托必须存字段，否则被 GC 回收后进程直接崩
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventObjectShow, EventObjectShow, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);

        if (_hook == IntPtr.Zero)
        {
            App.Log("自绘弹窗监听：WinEvent 钩子安装失败");
            return;
        }

        App.Log("自绘弹窗监听已启动（盯新的置顶小窗口）");

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        _ = UnhookWinEvent(_hook);
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != ObjidWindow || idChild != ChildidSelf || hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < MinWidth || width > MaxWidth || height < MinHeight || height > MaxHeight)
            {
                return;
            }

            var className = ClassNameOf(hwnd);
            if (IgnoredClasses.Any(ignored => className.StartsWith(ignored, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var process = ProcessNameOf(pid);
            if (process.Length == 0
                || IgnoredProcesses.Contains(process, StringComparer.OrdinalIgnoreCase)
                || string.Equals(process, OwnProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            var topmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
            var tool = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

            Diag($"候选窗口 cls={className} proc={process} {width}x{height} topmost={topmost} tool={tool}");

            // 消息弹窗都是置顶的；不置顶的小窗口大多是工具栏/浮动面板，别去读它们
            if (!topmost)
            {
                return;
            }

            Queue(hwnd, process, rect);
        }
        catch (Exception ex)
        {
            App.Log("弹窗事件处理失败：" + ex.Message);
        }
    }

    private void Queue(IntPtr hwnd, string process, NativeMethods.Rect rect)
    {
        lock (_mapGate)
        {
            if (_handled.TryGetValue(hwnd, out var last) && DateTime.Now - last < HandleCooldown)
            {
                return;
            }

            _handled[hwnd] = DateTime.Now;
            if (_handled.Count > 256)
            {
                var expired = _handled.Where(pair => DateTime.Now - pair.Value > HandleCooldown)
                    .Select(pair => pair.Key).ToList();
                foreach (var key in expired)
                {
                    _handled.Remove(key);
                }
            }
        }

        _ = ReadAsync(hwnd, process, rect);
    }

    private async Task ReadAsync(IntPtr hwnd, string process, NativeMethods.Rect rect)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Delay(PaintWaitMs);

            if (!IsWindowVisible(hwnd))
            {
                return;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var shot = Capture(rect.Left, rect.Top, width, height);
            if (shot is null)
            {
                return;
            }

            var lines = await ReadTextAsync(shot.Value);
            if (lines.Count == 0)
            {
                App.Log($"自绘弹窗没读到文字：{process} {width}x{height}");
                return;
            }

            Publish(new NotificationMessage
            {
                App = FriendlyName(process),
                Title = lines[0],
                Body = string.Join(" ", lines.Skip(1)),
            });
        }
        catch (Exception ex)
        {
            App.Log("自绘弹窗读取失败：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ————————————————————————— 文字识别 —————————————————————————

    /// <summary>
    /// 先试系统 OCR（中文机器上现成可用、不占内存），系统没有中文引擎或没读出内容时，
    /// 再回退到随包分发的 PP-OCRv5 内置模型。
    /// </summary>
    private async Task<List<string>> ReadTextAsync(Shot shot)
    {
        var engine = PickSystemEngine();
        if (engine is not null)
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                shot.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, shot.Width, shot.Height,
                BitmapAlphaMode.Premultiplied);

            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines
                .Select(line => Tidy(line.Text ?? ""))
                .Where(text => text.Length > 0)
                .ToList();

            if (lines.Count > 0)
            {
                return lines;
            }
        }

        return await Task.Run(() => OfflineRead(shot));
    }

    private static OcrEngine? PickSystemEngine()
    {
        var chinese = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));
        if (chinese is not null)
        {
            return chinese;
        }

        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is not null)
            {
                return engine;
            }
        }

        return null;
    }

    private List<string> OfflineRead(Shot shot)
    {
        var ocr = GetOfflineEngine();
        if (ocr is null)
        {
            return new List<string>();
        }

        using var bitmap = ToSkBitmap(shot);
        var result = ocr.Detect(bitmap, RapidOcrOptions.Default);

        return result.TextBlocks
            .Select(block => Tidy(block.Text ?? ""))
            .Where(text => text.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 系统 OCR 会在每个汉字之间塞一个空格（它把汉字当成一个个独立单词），
    /// 照原样显示就成了"小 张"。这里把两侧都是中日韩文字或全角标点的空格去掉，
    /// 中英文之间正常的分词空格保留。
    /// </summary>
    private static string Tidy(string text)
        => CjkSpaces.Replace(text.Trim(), "");

    private static readonly Regex CjkSpaces = new(
        @"(?<=[\u3400-\u9fff\u3000-\u303f\uff00-\uffef]) +(?=[\u3400-\u9fff\u3000-\u303f\uff00-\uffef])",
        RegexOptions.Compiled);

    /// <summary>内置离线引擎：和歌词那套同一批模型，首次真需要才加载。</summary>
    private RapidOcr? GetOfflineEngine()
    {
        if (_offlineTried)
        {
            return _offline;
        }

        _offlineTried = true;
        try
        {
            var offlineDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr");
            var recPath = Path.Combine(offlineDir, "ch_PP-OCRv5_rec_mobile.onnx");
            var dictPath = Path.Combine(offlineDir, "ch_PP-OCRv5_dict.txt");

            if (!File.Exists(recPath) || !File.Exists(dictPath))
            {
                App.Log($"弹窗 OCR：内置模型缺失 {recPath}");
                return null;
            }

            var latin = RapidOcrModelSet.PPOCRv5Latin;
            var ocr = new RapidOcr();
            ocr.InitModels(latin with
            {
                DetModelPath = Path.Combine(AppContext.BaseDirectory, latin.DetModelPath),
                ClsModelPath = Path.Combine(AppContext.BaseDirectory, latin.ClsModelPath),
                RecModelPath = recPath,
                KeysPath = dictPath,
            });

            App.Log("弹窗 OCR：内置离线模型已就绪");
            _offline = ocr;
        }
        catch (Exception ex)
        {
            App.Log("弹窗 OCR：内置离线模型加载失败：" + ex.Message);
        }

        return _offline;
    }

    private static SKBitmap ToSkBitmap(Shot shot)
    {
        // Opaque 而非 Premul：GDI 抓屏的 DIB 里 alpha 全是 0，按预乘处理会反预乘成全黑
        var info = new SKImageInfo(shot.Width, shot.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(shot.Bgra, 0, bitmap.GetPixels(), shot.Bgra.Length);
        return bitmap;
    }

    // ————————————————————————— 出岛 —————————————————————————

    private void Publish(NotificationMessage message)
    {
        if (message.Title.Length == 0 && message.Body.Length == 0)
        {
            return;
        }

        var key = message.Title + "|" + message.Body;
        lock (_mapGate)
        {
            if (_recentTexts.TryGetValue(key, out var last) && DateTime.Now - last < TextCooldown)
            {
                return;
            }

            _recentTexts[key] = DateTime.Now;
            if (_recentTexts.Count > 64)
            {
                _recentTexts.Clear();
            }
        }

        App.Log($"截到自绘弹窗：{message.Label} / {message.Title} / {message.Body}");

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() => Detected?.Invoke(message));
    }

    private void Diag(string text)
    {
        if (Interlocked.Increment(ref _diagnostics) > MaxDiagnostics)
        {
            return;
        }

        App.Log("[弹窗诊断] " + text);
    }

    private static string OwnProcessName
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return self.ProcessName;
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? "" : buffer.ToString(0, length);
    }

    /// <summary>把进程名换成看得懂的名字，认识的那几个才有中文。</summary>
    private static string FriendlyName(string process) => process.ToLowerInvariant() switch
    {
        "weixin" or "wechat" => "微信",
        "qq" or "qqnt" or "qqsc" or "qqim" => "QQ",
        "tim" => "TIM",
        "dingtalk" => "钉钉",
        "feishu" or "lark" => "飞书",
        "wxwork" or "wework" => "企业微信",
        "telegram" => "Telegram",
        "discord" => "Discord",
        _ => process,
    };

    // ————————————————————————— 抓屏 —————————————————————————

    /// <summary>
    /// 用 GDI 把屏幕上一块区域抓成 BGRA8 原始像素。放大 2 倍再识别：
    /// 弹窗上正文往往只有十几像素高，直接 OCR 中文准确率会明显下降。
    /// </summary>
    private static Shot? Capture(int x, int y, int width, int height)
    {
        const int scale = 2;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var outWidth = width * scale;
        var outHeight = height * scale;
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, outWidth, outHeight);
        var previous = SelectObject(memDc, bitmap);

        try
        {
            _ = SetStretchBltMode(memDc, Halftone);
            _ = SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);

            if (!StretchBlt(
                    memDc, 0, 0, outWidth, outHeight, screenDc, x, y, width, height, SrcCopy))
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Size = 40,
                Width = outWidth,
                Height = -outHeight, // 负高度 = 自上而下，省一次翻转
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var pixels = new byte[outWidth * outHeight * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (GetDIBits(memDc, bitmap, 0, (uint)outHeight, handle.AddrOfPinnedObject(), ref info, 0) == 0)
                {
                    return null;
                }
            }
            finally
            {
                handle.Free();
            }

            return new Shot(pixels, outWidth, outHeight);
        }
        finally
        {
            _ = SelectObject(memDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private readonly record struct Shot(byte[] Bgra, int Width, int Height);

    // ————————————————————————— Win32 —————————————————————————

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private const int SrcCopy = 0x00CC0020;
    private const int Halftone = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr dst, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}