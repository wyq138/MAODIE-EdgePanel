using System.Runtime.InteropServices.WindowsRuntime;
using EdgePanel.Core;
using EdgePanel.Pages;
using EdgePanel.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace EdgePanel;

/// <summary>
/// 贴边面板窗口：无边框 / 置顶 / 不占任务栏，鼠标移入滑出、移出收起，头部可拖动沿边缘移动。
/// </summary>
public sealed partial class PanelWindow : Window
{
    private sealed record NavEntry(string Glyph, string Label, string LabelEn, Type PageType);

    private static readonly NavEntry[] Catalog =
    {
        new("\uE80F", "概览", "Overview", typeof(OverviewPage)),
        new("\uE99A", "AI 助手", "AI Agent", typeof(AiPage)),
        new("\uE8BD", "电脑助手", "PC Agent", typeof(AssistantPage)),
        new("\uE8D6", "音乐歌词", "Music", typeof(MusicPage)),
        new("\uE7F4", "跑马灯", "Marquee", typeof(MarqueePage)),
        new("\uE8C8", "剪贴板", "Clipboard", typeof(ClipboardPage)),
        new("\uE916", "计时器", "Timer", typeof(TimerPage)),
        new("\uE9D9", "系统监测", "System", typeof(SystemPage)),
        new("\uE968", "网络配置", "Network", typeof(NetworkPage)),
        new("\uE71D", "快捷启动", "Launcher", typeof(LauncherPage)),
        new("\uE9CA", "天气", "Weather", typeof(WeatherPage)),
        new("\uE8A5", "待办清单", "Tasks", typeof(TodoPage)),
        new("\uE70B", "便签", "Notes", typeof(NotesPage)),
        new("\uE72E", "保险箱", "Vault", typeof(VaultPage)),
        new("\uE90F", "工具箱", "Toolbox", typeof(ToolsPage)),
        new("\uE713", "设置", "Settings", typeof(SettingsPage)),
    };

    private const int CollapsedWidthDip = 10;
    private const int CollapsedBarHeightDip = 132;

    // 半展开（竖向细长迷你播放条 / "岛"）：胶囊形，封面在上，均衡器 + 歌名 + 歌手 + 歌词在下
    private const int NowPlayingWidthDip = 48;
    private const int NowPlayingHeightDip = 236;

    private const int PanelHeightMaxPhysical = 880;
    private const int PanelHeightMarginPhysical = 72;

    private PanelSide _side;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    // 切歌瞬间 SMTC 会短暂"无会话/无标题"，先按住胶囊，等确认稳定消失再缩回小白条。
    // 早期只等 1.2s，播放器切歌稍慢就会先缩回小白条、下一首出来又弹成胶囊；
    // 拉长到 4s，覆盖切歌空档，只有真的没有会话（关闭播放器/长时间无播放）才收起。
    private readonly DispatcherTimer _nowPlayingHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(4000) };

    // 消息岛：截到一条系统弹窗就占用岛形态显示一会儿；连来多条时排队，一条一条放，不叠在一起。
    // 队列有上限，面板一直开着（收不到岛）时也不会无限堆积。
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly Queue<NotificationMessage> _messageQueue = new();
    private const int MessageQueueLimit = 5;
    private bool _message;
    private Storyboard? _messageStoryboard;

    // 窗口尺寸过渡：16ms 定时器 + 曲线插值，三态之间连续伸缩，不再"唰"地跳变。
    // 展开走 easeOutCubic（起步利落、收尾安定），收起走 smoothstep（两头都收住，退得稳）。
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private const double LayoutTransitionMs = 300;

    // 内容过渡：面板只做淡入淡出，不缩放也不位移。
    // 面板的尺寸在动画期间是钉死的（见 SyncExpandedFrame），位置也是钉死的，
    // 真正的"拉伸感"全部交给窗口这张帘子去做；面板自己再缩放/平移，反而会让它
    // 脱离屏幕边缘滑动，看起来像整块界面在飘。
    private const double PhasePanelFadeInMs = 160;
    private const double PhasePanelFadeOutMs = 110;
    private const double PhaseStripMs = 140;

    // 收起时窗口先自己缩，快缩成一条了面板才淡掉、收起条才淡进来，三段首尾相接、都在同一拍里收完。
    private const double PhasePanelFadeOutBeginMs = LayoutTransitionMs - PhasePanelFadeOutMs - 20;
    private const double PhaseStripInBeginMs = LayoutTransitionMs - PhaseStripMs - 20;

    // 收起态按住后移动超过这个距离才算"拖动"，否则松开时算"单击 → 展开"
    private const int DragThresholdPx = 4;

    private RectInt32 _layoutFrom;
    private RectInt32 _layoutTo;
    private DateTime _layoutStartedAt;
    private bool _firstLayoutDone;
    private bool _layoutShrinking;

    private bool _expanded;
    private bool _nowPlaying;
    private bool _dragging;
    private bool _capsulePressed;
    private bool _capsuleDragMoved;
    private int _dragStartCursorY;
    private int _dragStartWindowY;
    private int _pressCursorX;
    private int _pressCursorY;
    private int _pressWindowY;
    private double _scale = 1.0;
    private Storyboard? _phaseStoryboard;
    private DateTime _expandGraceUntil = DateTime.MinValue;

    private string _nowCoverFor = "";
    private bool _coverLoaded;
    private bool _coverLoading;
    private Storyboard? _equalizerStoryboard;
    private bool _equalizerOn;
    private DesktopAcrylicBackdrop? _acrylicBackdrop;

    public PanelWindow(AppSettings settings)
    {
        _side = settings.Side;
        _settings = settings;

        InitializeComponent();

        ConfigurePresenter();
        ApplyTheme();
        ApplySideChrome();
        BuildNavigation();

        Marquee.Text = settings.MarqueeText;
        Marquee.Speed = settings.MarqueeSpeed;
        Marquee.FontSize = settings.MarqueeFontSize;
        MarqueeHost.Visibility = settings.MarqueeEnabled ? Visibility.Visible : Visibility.Collapsed;

        Nav.SelectedItem = Nav.MenuItems[0];

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();

            Collapse();
        };

        // 展开后若干秒无操作 → 自动收回小白条。
        // 判断"无操作"必须排除正在使用弹出层的情况，否则打开下拉框/选目录对话框时
        // 鼠标离开主窗口就会触发 PointerExited，把面板从用户手里抢走。
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (!_dragging && !IsPinned && _settings.AutoCollapse && !IsPopupActive())
            {
                Collapse();
            }
        };

        // 胶囊态下切歌瞬间 SMTC 会报"无会话/无标题"，先按住 1.2s，确认稳定消失再缩回小白条
        _nowPlayingHoldTimer.Tick += (_, _) =>
        {
            _nowPlayingHoldTimer.Stop();
            if (_expanded)
            {
                return;
            }

            if (!HasSession(MediaService.Instance.Current) && _nowPlaying)
            {
                ApplyStripVisual();
                ApplyLayout(animate: true);
            }
        };

        Root.Loaded += Root_Loaded;

        // 尺寸过渡帧：从 _layoutFrom 向 _layoutTo 插值，200ms 收尾
        _layoutTimer.Tick += (_, _) => LayoutTick();

        // 媒体与歌词服务随面板启动，保证歌词随时可用
        _ = MediaService.Instance.InitializeAsync();
        // 在线歌词抓不到时，退回"截图 + OCR"读汽水迷你条上的那一行
        SodaLyrics.Instance.Start();

        // 消息截取：两条通道都汇到同一个消息岛。
        // NotificationWatcher 管注册进 Windows 通知中心的通知（系统弹窗），
        // PopupWatcher 管 QQ / 微信这类自己画窗口弹出来的消息（抓窗口像素做 OCR）。
        NotificationWatcher.Instance.Received += OnNotification;
        NotificationWatcher.Instance.Start();
        PopupWatcher.Instance.Detected += OnNotification;
        PopupWatcher.Instance.Start();

        _messageTimer.Tick += (_, _) => DismissMessage();

        // 收起成迷你播放条时，封面/歌名/歌词靠这两个事件驱动
        MediaService.Instance.Updated += OnMediaUpdated;
        LyricsService.Instance.Changed += OnLyricsChanged;
        SodaLyrics.Instance.Changed += OnSodaLyricChanged;
        Closed += (_, _) =>
        {
            MediaService.Instance.Updated -= OnMediaUpdated;
            LyricsService.Instance.Changed -= OnLyricsChanged;
            SodaLyrics.Instance.Changed -= OnSodaLyricChanged;
            NotificationWatcher.Instance.Received -= OnNotification;
            PopupWatcher.Instance.Detected -= OnNotification;
        };

        // WinUI 3 的 Window 必须显式激活才会显示
        Activate();
    }

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _scale = Root.XamlRoot?.RasterizationScale ?? 1.0;

        var hwnd = Hwnd;
        NativeMethods.MakeToolWindow(hwnd);
        // 去掉非客户区边框：否则深色胶囊四边会围一圈 3px 浅色框
        NativeMethods.RemoveWindowFrame(hwnd);
        NativeMethods.SetRoundedCorners(hwnd, _settings.Material != PanelMaterialKind.Solid);
        // 干掉 Win11 默认那圈浅色描边，否则深色胶囊周围会有一道白框
        NativeMethods.SetBorderColorNone(hwnd);

        // 控件（TextBox、Slider、按钮等）会把指针/键盘事件标记为已处理，
        // 普通 XAML 处理器收不到；用 handledEventsToo 兜底，保证面板内任何
        // 操作都会重置空闲计时，避免用户在便签里打字时面板到点自动收起。
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ResetIdle()), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => ResetIdle()), true);
        // 滚轮翻列表同样属于"正在操作"，早期漏了这个事件，用户滚动时会被判为空闲
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => ResetIdle()), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => ResetIdle()), true);

        ApplyStripVisual();
        ApplyLayout();
        App.Log($"面板已创建：{_side}，缩放 {_scale:0.##}，尺寸 {AppWindow.Size.Width}x{AppWindow.Size.Height}");
    }

    // ————————————————————————— 窗口配置 —————————————————————————

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    private void ApplySideChrome()
    {
        // 收起条 / 迷你播放条 / 消息岛都贴在窗口内的对应侧
        var align = _side == PanelSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        StripBorder.HorizontalAlignment = align;
        NowPlayingBorder.HorizontalAlignment = align;
        MessageBorder.HorizontalAlignment = align;

        // 收起条 / 消息岛的缩放以"贴着屏幕的那一侧"为锚点：左边缘取 x=0，右边缘取 x=1，
        // 这样它淡出/淡入时像是从边缘缩进去、长出来，而不是原地弹一下。
        // （展开态面板不再做缩放，它的尺寸和位置由 SyncExpandedFrame 钉死。）
        var originX = _side == PanelSide.Left ? 0.0 : 1.0;
        var origin = new Windows.Foundation.Point(originX, 0.5);
        CollapsedStrip.RenderTransformOrigin = origin;
        NowPlayingStrip.RenderTransformOrigin = origin;
        MessageStrip.RenderTransformOrigin = origin;

        CollapseButton.Content = new FontIcon
        {
            FontSize = 14,
            Glyph = _side == PanelSide.Left ? "\uE76B" : "\uE76C",
        };

        Title = $"EdgePanel · {SideText}";
        HeaderTitle.Text = $"EdgePanel · {SideText}";
    }

    private bool IsChinese => !(_settings.Language ?? "zh-CN")
        .StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private string SideText => IsChinese
        ? (_side == PanelSide.Left ? "左边缘" : "右边缘")
        : (_side == PanelSide.Left ? "Left edge" : "Right edge");

    // ————————————————————————— 主题（深色/浅色） —————————————————————————

    private bool IsDarkTheme => _settings.ThemeMode switch
    {
        AppThemeMode.Light => false,
        AppThemeMode.Dark => true,
        _ => IsSystemDark(),
    };

    private static bool IsSystemDark()
    {
        try
        {
            var ui = new UISettings();
            var c = ui.GetColorValue(UIColorType.Background);
            return (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) < 128;
        }
        catch
        {
            return true;
        }
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.ThemeMode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var dark = IsDarkTheme;

        // 胶囊（迷你播放条 / 消息岛）配色：深色用深底浅字，浅色用浅底深字
        var capsule = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 16, 18, 24)
            : Windows.UI.Color.FromArgb(255, 250, 251, 253));
        NowPlayingBorder.Background = capsule;
        MessageBorder.Background = capsule;

        MessageTitle.Foreground = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 246, 248, 255)
            : Windows.UI.Color.FromArgb(255, 24, 28, 40));

        NowCoverHost.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(0x22, 255, 255, 255)
            : Windows.UI.Color.FromArgb(0x22, 0, 0, 0));

        NowLyric.Foreground = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 127, 212, 255)
            : Windows.UI.Color.FromArgb(255, 20, 110, 200));

        ApplyMaterial();
    }

    private void ApplyMaterial()
    {
        try
        {
            var dark = IsDarkTheme;
            var baseColor = dark
                ? Colors.Black
                : Windows.UI.Color.FromArgb(255, 245, 246, 250);

            switch (_settings.Material)
            {
                case PanelMaterialKind.Mica:
                    SystemBackdrop = new MicaBackdrop();
                    GlassBorder.Background = WithOpacity(baseColor, _settings.Opacity * (dark ? 0.35 : 0.55));
                    SheenLayer.Visibility = Visibility.Collapsed;
                    break;

                case PanelMaterialKind.Solid:
                    SystemBackdrop = null;
                    GlassBorder.Background = WithOpacity(baseColor, Math.Max(dark ? 0.92 : 0.96, _settings.Opacity));
                    SheenLayer.Visibility = Visibility.Collapsed;
                    break;

                case PanelMaterialKind.Acrylic:
                    SystemBackdrop = AcrylicBackdrop;
                    GlassBorder.Background = WithOpacity(baseColor, _settings.Opacity * (dark ? 0.5 : 0.55));
                    SheenLayer.Visibility = Visibility.Collapsed;
                    break;

                default: // LiquidGlass
                    SystemBackdrop = AcrylicBackdrop;
                    GlassBorder.Background = WithOpacity(baseColor, _settings.Opacity * (dark ? 0.35 : 0.5));
                    SheenLayer.Visibility = Visibility.Visible;
                    break;
            }

            GlassBorder.CornerRadius = new CornerRadius(_settings.CornerRadius);
            SheenLayer.CornerRadius = new CornerRadius(_settings.CornerRadius);
        }
        catch (Exception ex)
        {
            App.Log("应用材质失败：" + ex.Message);
            SystemBackdrop = null;
            GlassBorder.Background = WithOpacity(Colors.Black, 0.9);
        }
    }

    /// <summary>缓存亚克力背景实例：收起/展开来回切换时复用同一个，避免反复新建拖慢展开。</summary>
    private DesktopAcrylicBackdrop AcrylicBackdrop => _acrylicBackdrop ??= new DesktopAcrylicBackdrop();

    private static SolidColorBrush WithOpacity(Windows.UI.Color color, double opacity)
        => new(Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(opacity * 255, 0, 255), color.R, color.G, color.B));

    private static class Colors
    {
        public static readonly Windows.UI.Color Black = Windows.UI.Color.FromArgb(255, 16, 18, 24);
    }

    // ————————————————————————— 布局与动画 —————————————————————————

    private RectInt32 WorkArea
    {
        get
        {
            // 多显示器：跟随鼠标所在显示器贴边
            if (_settings.FollowActiveMonitor && NativeMethods.GetCursorPos(out var cursor))
            {
                try
                {
                    return DisplayArea
                        .GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest)
                        .WorkArea;
                }
                catch
                {
                    // 取不到就退回窗口所在显示器
                }
            }

            return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        }
    }

    /// <summary>
    /// 计算"展开态面板"的纵向占位：完整高度与顶部 Y。
    /// 胶囊/细条都以此为锚点垂直居中，拖动时也用它把窗口位置换算回 OffsetY。
    /// </summary>
    private (int FullHeight, double FullY) ComputeFullFrame()
    {
        var area = WorkArea;

        var fullHeight = Math.Min(area.Height - PanelHeightMarginPhysical, PanelHeightMaxPhysical);
        if (fullHeight < 200)
        {
            fullHeight = area.Height;
        }

        var offset = _settings.OffsetY;
        var fullY = double.IsNaN(offset)
            ? area.Y + (area.Height - fullHeight) / 2
            : Math.Clamp(area.Y + (int)offset, area.Y, Math.Max(area.Y, area.Y + area.Height - fullHeight));

        return (fullHeight, fullY);
    }

    /// <summary>
    /// 按当前状态摆放窗口：展开 = 完整面板；半展开 = 迷你播放条；收起 = 贴边小白条。
    /// 三种状态都真实改变窗口尺寸，圆角另外交给 SetWindowRgn / DWM 处理（见 ApplyWindowShape）。
    /// </summary>
    private void ApplyLayout(bool animate = false)
    {
        var area = WorkArea;
        var (fullHeight, fullY) = ComputeFullFrame();

        // 三种状态都先把展开态面板按"最终尺寸"排好版（见 SyncExpandedFrame），
        // 之后窗口再怎么伸缩，面板内部都不需要重新排版。
        SyncExpandedFrame(fullHeight);

        int width;
        int height;
        int y;

        if (_expanded)
        {
            width = (int)Math.Round(_settings.PanelWidth * _scale);
            height = fullHeight;
            y = (int)fullY;
        }
        else if (_nowPlaying || _message)
        {
            width = (int)Math.Round(NowPlayingWidthDip * _scale);
            height = (int)Math.Round(NowPlayingHeightDip * _scale);
            // 与展开面板共用同一垂直中心，这样展开时鼠标依然落在面板内
            y = (int)fullY + (fullHeight - height) / 2;
        }
        else
        {
            width = (int)Math.Round(CollapsedWidthDip * _scale);
            height = (int)Math.Round(CollapsedBarHeightDip * _scale);
            y = (int)fullY + (fullHeight - height) / 2;
        }

        var x = _side == PanelSide.Left ? area.X : area.X + area.Width - width;

        var target = new RectInt32(x, Math.Max(area.Y, y), Math.Max(4, width), height);

        // 首次布局直接落位：此时窗口刚显示，走动画会从 0 尺寸"长"出来
        if (!animate || !_firstLayoutDone)
        {
            _firstLayoutDone = true;
            _layoutTimer.Stop();
            AppWindow.MoveAndResize(target);
            ApplyWindowShape();
            return;
        }

        // 从当前实际矩形平滑插值到目标矩形，避免三态切换"唰"地瞬间跳变
        _layoutFrom = new RectInt32(
            AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _layoutTo = target;
        // 变宽（展开）用减速曲线，变窄（收起）用平滑曲线，两个方向的手感各自合适
        _layoutShrinking = target.Width < AppWindow.Size.Width;
        _layoutStartedAt = DateTime.UtcNow;
        if (!_layoutTimer.IsEnabled)
        {
            _layoutTimer.Start();
        }
    }

    /// <summary>尺寸过渡的一帧：按方向选曲线，插值窗口矩形。</summary>
    private void LayoutTick()
    {
        var t = (DateTime.UtcNow - _layoutStartedAt).TotalMilliseconds / LayoutTransitionMs;
        if (t >= 1)
        {
            _layoutTimer.Stop();
            AppWindow.MoveAndResize(_layoutTo);
            ApplyWindowShape();
            return;
        }

        static int Lerp(int a, int b, double k) => (int)Math.Round(a + (b - a) * k);
        var e = _layoutShrinking
            ? t * t * (3 - 2 * t)              // smoothstep：两头都收住，退得稳
            : 1 - Math.Pow(1 - t, 3);          // easeOutCubic：起步利落、收尾安定

        var rect = new RectInt32(
            Lerp(_layoutFrom.X, _layoutTo.X, e),
            Lerp(_layoutFrom.Y, _layoutTo.Y, e),
            Lerp(_layoutFrom.Width, _layoutTo.Width, e),
            Lerp(_layoutFrom.Height, _layoutTo.Height, e));

        AppWindow.MoveAndResize(rect);

        // 窗口尺寸每帧都在变，裁剪区域必须跟着重算，否则胶囊的圆角会"跟不上"而露方角。
        // 展开态若用的是带背景层的材质（亚克力/云母）则不裁剪，交给 DWM 圆角。
        if (NeedsRegion)
        {
            ApplyRegionToWindow(rect.Width, rect.Height);
        }
    }

    /// <summary>
    /// 当前状态是否需要自己裁剪窗口区域。
    /// 只有窗口底色不透明时（收起态，或展开态选了纯色材质）才需要——
    /// 不透明底色会把 XAML 圆角以外的方角露出来。
    /// </summary>
    private bool NeedsRegion => !_expanded || _settings.Material == PanelMaterialKind.Solid;

    /// <summary>把窗口裁成圆角：展开态用设置的圆角半径，胶囊/细条态用 min(宽,高)/2（两头半圆）。</summary>
    private void ApplyRegionToWindow(int width, int height)
    {
        var radius = _expanded
            ? (int)Math.Round(_settings.CornerRadius * _scale)
            : Math.Min(width, height) / 2;
        NativeMethods.SetRoundedRegion(Hwnd, width, height, radius);
    }

    /// <summary>
    /// 摆好窗口的圆角：
    /// - 展开态 + 带背景层的材质：去掉裁剪区域，交给 DWM 圆角（连背景层一起裁圆）；
    /// - 其余情况（胶囊/细条，或纯色底的展开态）：窗口底色是不透明的主题纯色，
    ///   仅靠 XAML 的 CornerRadius 圆角外会露出方角，必须用 SetWindowRgn 把窗口裁成圆角。
    /// </summary>
    private void ApplyWindowShape()
    {
        if (!NeedsRegion)
        {
            NativeMethods.ClearRegion(Hwnd);
            NativeMethods.SetRoundedCorners(Hwnd, true);
            return;
        }

        NativeMethods.SetRoundedCorners(Hwnd, false);
        var size = AppWindow.Size;
        ApplyRegionToWindow(size.Width, size.Height);
    }

    /// <summary>该快照是否有可显示的播放会话（播放中或暂停但会话还在）。</summary>
    private static bool HasSession(MediaInfo info)
        => info.HasSession && !string.IsNullOrWhiteSpace(info.Title);

    /// <summary>决定收起时落到"迷你播放条"还是"小白条"，并填充内容。</summary>
    private void ApplyStripVisual()
    {
        // 胶囊/细条态禁用亚克力背景：DWM 背景层不跟窗口裁剪，圆角外会残留一圈白边
        SystemBackdrop = null;

        var showNowPlaying = HasSession(MediaService.Instance.Current);
        _nowPlaying = showNowPlaying;

        if (showNowPlaying)
        {
            FillNowPlaying(MediaService.Instance.Current);
        }

        // 消息优先于音乐：截到消息时先把岛让给消息，退场后再回音乐岛
        var shown = _message ? MessageStrip
            : showNowPlaying ? NowPlayingStrip
            : CollapsedStrip;

        MessageStrip.Visibility = ReferenceEquals(shown, MessageStrip) ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingStrip.Visibility = ReferenceEquals(shown, NowPlayingStrip) ? Visibility.Visible : Visibility.Collapsed;
        CollapsedStrip.Visibility = ReferenceEquals(shown, CollapsedStrip) ? Visibility.Visible : Visibility.Collapsed;

        // 显示的那条复位为"完全显示"，否则刚启动 / 上一次动画残留会把它留在透明或缩小状态
        shown.Opacity = 1;
        var scale = StripScaleFor(shown);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private void FillNowPlaying(MediaInfo info)
    {
        NowTitle.Text = info.Title;
        NowArtist.Text = string.IsNullOrWhiteSpace(info.Artist) ? info.Album : info.Artist;

        // 没打开音乐页时这里也要把歌词拉起来，否则迷你条永远只有歌名
        if (!string.IsNullOrWhiteSpace(info.Title) && LyricsService.Instance.MatchedTitle != info.Title)
        {
            _ = LyricsService.Instance.LoadForAsync(info.Title, info.Artist);
        }

        NowLyric.Text = CurrentLyricText(info.Position);
        UpdateEqualizer(info.IsPlaying);

        // 换歌先清空封面与加载状态，避免残留上一首
        if (_nowCoverFor != info.Title)
        {
            _nowCoverFor = info.Title;
            _coverLoaded = false;
            _coverLoading = false;
            NowCover.Source = null;
        }

        // SMTC 首发常常还没有缩略图，所以封面在"拿到字节 + 尚未加载成功"时持续重试，
        // 早期只在换歌那一刻读一次，导致偶尔整首都没有封面。
        if (!_coverLoaded && !_coverLoading && info.Thumbnail is { Length: > 0 } bytes)
        {
            _ = LoadCoverAsync(bytes);
        }
    }

    private async Task LoadCoverAsync(byte[] bytes)
    {
        _coverLoading = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            NowCover.Source = image;
            _coverLoaded = true;
        }
        catch (Exception ex)
        {
            App.Log("迷你播放条封面加载失败：" + ex.Message);
        }
        finally
        {
            _coverLoading = false;
        }
    }

    /// <summary>播放中让均衡器条跳动；暂停/无会话时压平停住。</summary>
    private void UpdateEqualizer(bool playing)
    {
        if (playing == _equalizerOn)
        {
            return;
        }

        _equalizerOn = playing;
        _equalizerStoryboard?.Stop();
        _equalizerStoryboard = null;

        var bars = new[] { EqBar1, EqBar2, EqBar3, EqBar4 };

        if (!playing)
        {
            foreach (var bar in bars)
            {
                bar.ScaleY = 0.25;
            }

            NowEqualizer.Opacity = 0.35;
            return;
        }

        NowEqualizer.Opacity = 1;
        var storyboard = new Storyboard();
        for (var i = 0; i < bars.Length; i++)
        {
            var anim = new DoubleAnimation
            {
                From = 0.25,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(360)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 80),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, bars[i]);
            Storyboard.SetTargetProperty(anim, "ScaleY");
            storyboard.Children.Add(anim);
        }

        _equalizerStoryboard = storyboard;
        storyboard.Begin();
    }

    private static string CurrentLyricText(TimeSpan position)
    {
        var lyrics = LyricsService.Instance;
        if (!lyrics.HasLyrics)
        {
            // 兜底：汽水迷你条上实时显示的那一行（截图 + OCR 读出来的）
            var ocr = SodaLyrics.Instance.CurrentLine;
            return string.IsNullOrWhiteSpace(ocr) ? "暂无歌词" : ocr;
        }

        var index = lyrics.IndexAt(position);
        if (index < 0)
        {
            index = 0;
        }

        // LRC 里常有空行，往前找到最近一句有内容的
        for (var i = index; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lyrics.Lines[i].Text))
            {
                return lyrics.Lines[i].Text;
            }
        }

        return lyrics.Lines[index].Text;
    }

    /// <summary>歌词加载完成后刷新迷你播放条上的那一行。</summary>
    private void OnLyricsChanged() => RefreshNowLyric();

    /// <summary>迷你条 OCR 兜底歌词变化后刷新那一行。</summary>
    private void OnSodaLyricChanged() => RefreshNowLyric();

    private void RefreshNowLyric()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_expanded || !_nowPlaying)
            {
                return;
            }

            NowLyric.Text = CurrentLyricText(MediaService.Instance.Current.Position);
        });
    }

    // ————————————————————————— 消息岛 —————————————————————————

    /// <summary>消息角标配色：同一应用固定取同一组，够跳但不刺眼。</summary>
    private static readonly (uint Start, uint End)[] MessagePalette =
    {
        (0xFF3B82F6, 0xFF8B5CF6),
        (0xFF06B6D4, 0xFF3B82F6),
        (0xFFF97316, 0xFFEF4444),
        (0xFF10B981, 0xFF06B6D4),
        (0xFFEC4899, 0xFF8B5CF6),
        (0xFFFACC15, 0xFFF97316),
    };

    private void OnNotification(NotificationMessage message)
    {
        if (_messageQueue.Count >= MessageQueueLimit)
        {
            _messageQueue.Dequeue();
        }

        _messageQueue.Enqueue(message);
        TryShowMessage();
    }

    /// <summary>把队列里最前面那条放到岛上。面板正开着、或岛上已经有消息时不抢。</summary>
    private void TryShowMessage()
    {
        if (_expanded || _message || _messageQueue.Count == 0)
        {
            return;
        }

        ShowMessage(_messageQueue.Dequeue());
    }

    private void ShowMessage(NotificationMessage message)
    {
        FillMessage(message);

        _message = true;
        _messageTimer.Stop();

        ApplyStripVisual();
        ApplyLayout(animate: true);
        PlayMessageEntrance();

        _messageTimer.Start();
        App.Log($"消息岛已显示：{message.Label} / {message.Title}");
    }

    /// <summary>到点退场：队列里还有就接着放下一条，没有就交回给音乐岛 / 小白条。</summary>
    private void DismissMessage()
    {
        _messageTimer.Stop();
        if (!_message)
        {
            return;
        }

        if (_messageQueue.Count > 0)
        {
            ShowMessage(_messageQueue.Dequeue());
            return;
        }

        PlayMessageExit(() =>
        {
            _message = false;
            ApplyStripVisual();
            ApplyLayout(animate: true);
        });
    }

    private void FillMessage(NotificationMessage message)
    {
        var (start, end) = AccentFor(message.Label);

        MessageApp.Text = message.Label;
        MessageTitle.Text = string.IsNullOrWhiteSpace(message.Title) ? message.Body : message.Title;
        MessageBody.Text = message.Body == MessageTitle.Text ? "" : message.Body;
        MessageBadgeText.Text = BadgeText(message.Label);

        MessageBadge.Background = Gradient(start, end);
        MessageGlow.BorderBrush = Gradient(end, start);
    }

    /// <summary>消息岛入场：弹入 + 循环扫光 + 描边呼吸。</summary>
    private void PlayMessageEntrance()
    {
        _messageStoryboard?.Stop();

        MessageStrip.Opacity = 0;
        MessageScale.ScaleX = 0.72;
        MessageScale.ScaleY = 0.72;
        MessageSweepTranslate.Y = SweepOffset;
        MessageGlow.Opacity = 0.25;

        var sb = new Storyboard();

        sb.Children.Add(Anim(MessageStrip, "Opacity", 0, 1, 180, EasingMode.EaseOut));

        // 弹入：BackEase 让尺寸冲过 1 再回落，比单纯放大有精神
        sb.Children.Add(Back(MessageScale, "(ScaleTransform.ScaleX)", 0.72, 1));
        sb.Children.Add(Back(MessageScale, "(ScaleTransform.ScaleY)", 0.72, 1));

        // 扫光在胶囊内部上下循环；描边同时呼吸，让岛在边缘"活着"
        sb.Children.Add(Loop(MessageSweepTranslate, "(TranslateTransform.Y)", SweepOffset, -SweepOffset, 1300, beginMs: 240));
        sb.Children.Add(Loop(MessageGlow, "Opacity", 0.25, 0.9, 1100, beginMs: 0));

        sb.Begin();
        _messageStoryboard = sb;
    }

    /// <summary>消息岛退场：缩回边缘并淡掉，交回给音乐岛 / 小白条。</summary>
    private void PlayMessageExit(Action done)
    {
        _messageStoryboard?.Stop();
        _messageStoryboard = null;

        var sb = new Storyboard();
        sb.Children.Add(Anim(MessageStrip, "Opacity", 1, 0, 170, EasingMode.EaseIn));
        sb.Children.Add(Anim(MessageScale, "(ScaleTransform.ScaleX)", 1, 0.8, 170, EasingMode.EaseIn));
        sb.Children.Add(Anim(MessageScale, "(ScaleTransform.ScaleY)", 1, 0.8, 170, EasingMode.EaseIn));
        sb.Completed += (_, _) => done();
        sb.Begin();
    }

    /// <summary>扫光条的位移幅度：留足余量，保证光带始终待在胶囊内部。</summary>
    private const double SweepOffset = 62;

    private static DoubleAnimation Back(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(460)),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static DoubleAnimation Loop(
        DependencyObject target, string property, double from, double to, double ms, double beginMs)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static LinearGradientBrush Gradient(uint from, uint to)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Argb(from) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Argb(to) });
        return brush;
    }

    private static Windows.UI.Color Argb(uint value)
        => Windows.UI.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>来源应用固定取同一组配色，同一个应用每次都是同一种颜色。</summary>
    private static (uint Start, uint End) AccentFor(string app)
    {
        var hash = 0;
        foreach (var c in app)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return MessagePalette[hash % MessagePalette.Length];
    }

    /// <summary>角标里的字：中文取第一个字，英文取首字母大写。</summary>
    private static string BadgeText(string app)
    {
        var text = app.Trim();
        if (text.Length == 0)
        {
            return "信";
        }

        return char.ToUpperInvariant(text[0]).ToString();
    }

    /// <summary>媒体信息变化（可能来自后台线程），同步迷你播放条。</summary>
    private void OnMediaUpdated(MediaInfo info)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_expanded)
            {
                return;
            }

            if (HasSession(info))
            {
                // 会话回来了，取消"无会话"计时
                _nowPlayingHoldTimer.Stop();
                FillNowPlaying(info);

                if (!_nowPlaying)
                {
                    ApplyStripVisual();
                    ApplyLayout(animate: true);
                }
            }
            else if (_nowPlaying)
            {
                // 切歌瞬间 SMTC 可能报"无会话/无标题"，先按住，等确认稳定消失再缩回小白条
                if (!_nowPlayingHoldTimer.IsEnabled)
                {
                    _nowPlayingHoldTimer.Start();
                }
            }
        });
    }

    /// <summary>
    /// 把展开态面板的尺寸与位置钉死在"最终面板尺寸 / 贴着屏幕那一侧 / 垂直居中"上。
    /// 窗口的中心与完整面板的中心是重合的（见 ComputeFullFrame 与 ApplyLayout 的 y 计算），
    /// 所以只要面板居中固定，窗口从 10x132 长到 392x880 的过程中，面板本身不移动也不重排，
    /// 只是被"帘子"一点点露出来。宽度用的是设置里的 DIP 值，高度把物理高度换算回 DIP。
    /// </summary>
    private void SyncExpandedFrame(int fullHeightPhysical)
    {
        ExpandedHost.Width = _settings.PanelWidth;
        ExpandedHost.Height = Math.Round(fullHeightPhysical / _scale);
        ExpandedHost.HorizontalAlignment = _side == PanelSide.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        ExpandedHost.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>当前可见的收起态元素（消息岛 / 迷你播放条 / 小白条），没有则返回 null。</summary>
    private FrameworkElement? CurrentStrip
        => MessageStrip.Visibility == Visibility.Visible ? MessageStrip
            : CollapsedStrip.Visibility == Visibility.Visible ? CollapsedStrip
            : NowPlayingStrip.Visibility == Visibility.Visible ? NowPlayingStrip
            : null;

    private ScaleTransform StripScaleFor(FrameworkElement strip)
        => ReferenceEquals(strip, MessageStrip) ? MessageScale
            : ReferenceEquals(strip, NowPlayingStrip) ? NowPlayingScale
            : StripScale;

    /// <summary>构造一个带缓动的 DoubleAnimation 并挂到 Storyboard 目标上。</summary>
    private static DoubleAnimation Anim(
        DependencyObject target, string property,
        double from, double to, double ms, EasingMode mode, double beginMs = 0)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new CubicEase { EasingMode = mode },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private void Expand()
    {
        if (_expanded)
        {
            return;
        }

        // 上一次收起的动画可能还在跑，先掐掉，避免两边争夺同一批变换
        _phaseStoryboard?.Stop();
        _phaseStoryboard = null;

        // 收起点（胶囊/小白条）在展开过程中同步淡出、缩小，像被面板"吸"进去
        var strip = CurrentStrip;
        var stripOpacity = strip?.Opacity ?? 0;

        _expanded = true;
        _nowPlaying = false;

        // 岛上的消息只是"瞥一眼"：用户点开面板说明要看内容了，消息立刻退场
        if (_message)
        {
            _messageTimer.Stop();
            _message = false;
            _messageStoryboard?.Stop();
            _messageStoryboard = null;
        }

        // 改窗口尺寸会引起重排，可能瞬间发出 PointerExited；给一小段宽限期，
        // 否则刚展开就被"鼠标移开"逻辑收回去。
        _expandGraceUntil = DateTime.UtcNow.AddMilliseconds(500);
        _collapseTimer.Stop();
        _idleTimer.Stop();

        // 收起态把 SystemBackdrop 置空了，展开时要还原；复用缓存的亚克力实例，几乎不耗时
        ApplyMaterial();

        // 先清掉胶囊那会儿的小裁剪区域，否则长大的面板会被旧区域裁掉一半
        NativeMethods.ClearRegion(Hwnd);

        // 内容就位（此刻窗口还是旧尺寸，随后靠尺寸过渡"拉开帘子"把面板露出来）
        ExpandedHost.Visibility = Visibility.Visible;
        ExpandedHost.Opacity = 0;

        ApplyWindowShape();
        ApplyLayout(animate: true);

        var sb = new Storyboard();

        // 面板：只淡入，不改尺寸也不位移。窗口这张帘子一拉开，面板就是它最终的样子，
        // 内部排版在动画开始前就已经定死，所以全程看不到任何重排。
        sb.Children.Add(Anim(ExpandedHost, "Opacity", 0, 1, PhasePanelFadeInMs, EasingMode.EaseOut));

        // 收起条：原地淡出并略微缩小，被面板盖过去
        if (strip is not null)
        {
            var stripScale = StripScaleFor(strip);
            sb.Children.Add(Anim(strip, "Opacity", stripOpacity, 0, PhaseStripMs, EasingMode.EaseIn));
            sb.Children.Add(Anim(stripScale, "(ScaleTransform.ScaleX)", 1, 0.92, PhaseStripMs, EasingMode.EaseIn));
            sb.Children.Add(Anim(stripScale, "(ScaleTransform.ScaleY)", 1, 0.92, PhaseStripMs, EasingMode.EaseIn));
        }

        sb.Completed += (_, _) =>
        {
            CollapsedStrip.Visibility = Visibility.Collapsed;
            NowPlayingStrip.Visibility = Visibility.Collapsed;
            CollapsedStrip.Opacity = 0;
            NowPlayingStrip.Opacity = 0;
        };

        _phaseStoryboard = sb;
        sb.Begin();

        ResetIdle();
    }

    private void Collapse()
    {
        if (!_expanded)
        {
            return;
        }

        // 上一次展开的动画可能还在跑，先掐掉，避免两边争夺同一批变换
        _phaseStoryboard?.Stop();
        _phaseStoryboard = null;

        _expanded = false;
        _idleTimer.Stop();
        _collapseTimer.Stop();

        // 决定收起后落成"迷你播放条"还是"小白条"，并填充内容；
        // 与面板的淡出/收窄同步进行，避免中间露出一帧空面板。
        ApplyStripVisual();

        var strip = CurrentStrip;

        // 目标条先就位、但透明且压扁，等面板退场时再"弹"出来
        if (strip is not null)
        {
            var scale = StripScaleFor(strip);
            strip.Visibility = Visibility.Visible;
            strip.Opacity = 0;
            scale.ScaleX = 0.92;
            scale.ScaleY = 0.92;
        }

        ApplyWindowShape();
        ApplyLayout(animate: true);

        // 明确摆回"完全展开"的起点，避免依赖上一段动画残留的属性值
        ExpandedHost.Visibility = Visibility.Visible;
        ExpandedHost.Opacity = 1;

        var sb = new Storyboard();

        // 面板：窗口自己先缩，缩到快剩一条时才淡掉，而且全程保持原样不重排——
        // 视觉上就是这块面板被帘子盖回去，中途不会露出一块"该收没收的空面板"。
        sb.Children.Add(Anim(
            ExpandedHost, "Opacity", 1, 0, PhasePanelFadeOutMs, EasingMode.EaseIn, PhasePanelFadeOutBeginMs));

        // 收起条：最后一段淡入 + 由压扁展开到原尺寸，跟窗口缩到底同步收尾
        if (strip is not null)
        {
            var stripScale = StripScaleFor(strip);
            sb.Children.Add(Anim(strip, "Opacity", 0, 1, PhaseStripMs, EasingMode.EaseOut, PhaseStripInBeginMs));
            sb.Children.Add(Anim(stripScale, "(ScaleTransform.ScaleX)", 0.92, 1, PhaseStripMs, EasingMode.EaseOut, PhaseStripInBeginMs));
            sb.Children.Add(Anim(stripScale, "(ScaleTransform.ScaleY)", 0.92, 1, PhaseStripMs, EasingMode.EaseOut, PhaseStripInBeginMs));
        }

        sb.Completed += (_, _) =>
        {
            ExpandedHost.Visibility = Visibility.Collapsed;
            ExpandedHost.Opacity = 0;

            // 结算一次，确保最终只剩正确的那条可见且复位
            ApplyStripVisual();
            if (CurrentStrip is { } settled)
            {
                settled.Opacity = 1;
                StripScaleFor(settled).ScaleX = 1;
                StripScaleFor(settled).ScaleY = 1;
            }

            // 面板开着的时候排队的消息现在可以放到岛上了（同一帧内切换，看不到中间态）
            TryShowMessage();
        };

        _phaseStoryboard = sb;
        sb.Begin();
    }

    private bool IsPinned => _settings.Pinned;

    // ————————————————————————— 交互 —————————————————————————

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _collapseTimer.Stop();
        ResetIdle();
    }

    /// <summary>
    /// 收起态按下胶囊：先记下起点，之后超过阈值才算拖动，否则松手时算单击 → 展开。
    /// 展开态的按下只是重置空闲计时（拖动由头部自己处理）。
    /// </summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_expanded)
        {
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                _capsulePressed = true;
                _capsuleDragMoved = false;
                _pressCursorX = cursor.X;
                _pressCursorY = cursor.Y;
                _pressWindowY = AppWindow.Position.Y;
                Root.CapturePointer(e.Pointer);
            }

            e.Handled = true;
            return;
        }

        ResetIdle();
    }

    private void ResetIdle()
    {
        _idleTimer.Stop();
        if (_expanded && _settings.AutoCollapse)
        {
            _idleTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.IdleCollapseSeconds));
            _idleTimer.Start();
        }
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _capsulePressed || !_settings.AutoCollapse || IsPinned)
        {
            return;
        }

        // 刚展开时的重排抖动不算"鼠标移开"
        if (DateTime.UtcNow < _expandGraceUntil)
        {
            return;
        }

        // 指针只是滑到某个子控件（按钮/列表/文本）上时，根元素的 PointerExited 也会触发；
        // 只要光标还在窗口内就不算"离开面板"，别急着收起。
        if (IsCursorInsideWindow())
        {
            return;
        }

        // 下拉框、日期选择、文件选择器、模态对话框都宿主在本进程的另一个窗口上，
        // 指针这时确实"离开"了主窗口，但用户正在操作面板，绝不能被收起。
        if (IsPopupActive())
        {
            return;
        }

        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, _settings.CollapseDelayMs));
        _collapseTimer.Start();
    }

    /// <summary>光标当前是否还在本窗口矩形内（用于区分"滑到子控件"和"真的离开面板"）。</summary>
    private bool IsCursorInsideWindow()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return true;
        }

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        return cursor.X >= pos.X && cursor.X < pos.X + size.Width &&
               cursor.Y >= pos.Y && cursor.Y < pos.Y + size.Height;
    }

    /// <summary>面板是否正处于"用户正在操作"的状态：焦点在本面板内，或本进程有弹出层/模态对话框在前台。</summary>
    private bool IsPopupActive()
    {
        try
        {
            var hwnd = Hwnd;

            // 被模态对话框（例如选择文件夹）禁用时，主窗口已不接收输入
            if (!NativeMethods.IsWindowEnabled(hwnd))
            {
                return true;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == hwnd)
            {
                return false;
            }

            // 前台窗口属于本进程但不是主窗口 → 说明是下拉框/浮出控件/对话框
            _ = NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _dragging = true;
        _dragStartCursorY = cursor.Y;
        _dragStartWindowY = AppWindow.Position.Y;
        Header.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        // 收起态：按住胶囊移动超过阈值后转入拖动
        if (_capsulePressed && !_expanded)
        {
            if (!_capsuleDragMoved &&
                (Math.Abs(cursor.X - _pressCursorX) >= DragThresholdPx ||
                 Math.Abs(cursor.Y - _pressCursorY) >= DragThresholdPx))
            {
                _capsuleDragMoved = true;
                _dragging = true;
            }

            if (_capsuleDragMoved)
            {
                DragTo(cursor, _pressWindowY, _pressCursorY);
            }

            return;
        }

        if (_dragging)
        {
            DragTo(cursor, _dragStartWindowY, _dragStartCursorY);
        }
    }

    /// <summary>
    /// 拖动：纵向跟随光标，横向只在左/右边缘之间跳（绝不吸附到顶部/底部，也不会悬在屏幕中间）。
    /// startWindowY / startCursorY 是按下瞬间的窗口 Y 与光标 Y，用来算相对位移。
    /// </summary>
    private void DragTo(NativeMethods.POINT cursor, int startWindowY, int startCursorY)
    {
        var area = WorkArea;
        var height = AppWindow.Size.Height;
        var width = AppWindow.Size.Width;

        var centerX = area.X + area.Width / 2;
        var side = cursor.X < centerX ? PanelSide.Left : PanelSide.Right;
        var x = side == PanelSide.Left ? area.X : area.X + area.Width - width;

        var targetY = startWindowY + (cursor.Y - startCursorY);
        targetY = Math.Clamp(targetY, area.Y, Math.Max(area.Y, area.Y + area.Height - height));

        if (side != _side)
        {
            _side = side;
            ApplySideChrome();
        }

        AppWindow.Move(new PointInt32(x, targetY));
        ResetIdle();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // 收起态：拖动结束 → 吸附并记位置；没拖动 → 视为单击，展开面板
        if (_capsulePressed && !_expanded)
        {
            _capsulePressed = false;
            Root.ReleasePointerCapture(e.Pointer);
            _dragging = false;

            if (_capsuleDragMoved)
            {
                _capsuleDragMoved = false;
                CommitDrag();
            }
            else
            {
                Expand();
            }

            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Header.ReleasePointerCapture(e.Pointer);
        CommitDrag();
    }

    /// <summary>拖动结束：吸附到最近边缘、把当前位置换算成 OffsetY 存回设置。</summary>
    private void CommitDrag()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var area = WorkArea;
        var (fullHeight, _) = ComputeFullFrame();

        // 窗口 Y 是"当前这一态"的顶部；展开态窗口即面板，胶囊/细条则要从窗口 Y 反推锚点 Y
        var windowY = AppWindow.Position.Y;
        double fullY;
        if (_expanded)
        {
            fullY = windowY;
        }
        else
        {
            var stripHeight = _nowPlaying
                ? (int)Math.Round(NowPlayingHeightDip * _scale)
                : (int)Math.Round(CollapsedBarHeightDip * _scale);
            fullY = windowY - (fullHeight - stripHeight) / 2.0;
        }

        fullY = Math.Clamp(fullY, area.Y, Math.Max(area.Y, area.Y + area.Height - fullHeight));
        _settings.OffsetY = (int)Math.Round(fullY - area.Y);

        // 松手后吸附到最近的边缘：光标在显示器左半边 → 左边缘，右半边 → 右边缘
        var centerX = area.X + area.Width / 2;
        _side = cursor.X < centerX ? PanelSide.Left : PanelSide.Right;
        _settings.Side = _side;

        ApplySideChrome();
        SettingsStore.Save();
        ApplyLayout(animate: true);
        ResetIdle();
        App.Log($"面板吸附到{(_side == PanelSide.Left ? "左" : "右")}边缘");
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _capsulePressed = false;
        _capsuleDragMoved = false;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Pinned = !IsPinned;
        SettingsStore.Save();
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        if (PinButton.Content is FontIcon icon)
        {
            icon.Glyph = IsPinned ? "\uE77A" : "\uE718";
            icon.Foreground = IsPinned
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 190, 255))
                : null;
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => Collapse();

    // ————————————————————————— 导航 —————————————————————————

    private void BuildNavigation()
    {
        foreach (var entry in Catalog)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = IsChinese ? entry.Label : entry.LabelEn,
                Tag = entry.PageType,
                Icon = new FontIcon { Glyph = entry.Glyph },
            });
        }

        UpdatePinVisual();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: Type pageType } &&
            ContentFrame.CurrentSourcePageType != pageType)
        {
            try
            {
                ContentFrame.Navigate(pageType, _side);
            }
            catch (Exception ex)
            {
                // 单个页面加载失败不应该拖垮整个侧边栏进程
                App.Log($"打开页面 {pageType.Name} 失败：{ex}");
            }
        }
    }


    /// <summary>供页面调用：界面语言切换后立即刷新导航标签与标题。</summary>
    public void ApplyLanguage()
    {
        for (var i = 0; i < Catalog.Length && i < Nav.MenuItems.Count; i++)
        {
            if (Nav.MenuItems[i] is NavigationViewItem item)
            {
                item.Content = IsChinese ? Catalog[i].Label : Catalog[i].LabelEn;
            }
        }

        ApplySideChrome();
    }

    /// <summary>供页面调用：切换材质/宽度等设置后立即刷新窗口。</summary>
    public void ApplySettings()
    {
        ApplyTheme();
        ApplyLayout(animate: true);
        Marquee.Text = _settings.MarqueeText;
        Marquee.Speed = _settings.MarqueeSpeed;
        Marquee.FontSize = _settings.MarqueeFontSize;
        MarqueeHost.Visibility = _settings.MarqueeEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
}