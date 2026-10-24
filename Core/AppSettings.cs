using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgePanel.Core;

/// <summary>面板贴靠的屏幕侧。</summary>
public enum PanelSide
{
    Left,
    Right,
}

/// <summary>可选的窗口材质。</summary>
public enum PanelMaterialKind
{
    /// <summary>亚克力（DesktopAcrylicBackdrop）——系统级模糊。</summary>
    Acrylic,

    /// <summary>液态玻璃：亚克力底 + 高光渐变 + 噪点 + 动态流光。</summary>
    LiquidGlass,

    /// <summary>云母材质。</summary>
    Mica,

    /// <summary>纯色（不透明）。</summary>
    Solid,
}

/// <summary>界面主题（深色/浅色/跟随系统）。</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>应用全部可持久化配置。</summary>
public sealed class AppSettings
{
    // —— 面板布局 ——
    /// <summary>是否启用贴边面板。</summary>
    public bool PanelEnabled { get; set; } = true;

    /// <summary>面板当前停靠的屏幕侧（可从左拖动到右）。</summary>
    public PanelSide Side { get; set; } = PanelSide.Left;

    /// <summary>展开后的面板宽度（像素）。</summary>
    public int PanelWidth { get; set; } = 392;

    /// <summary>面板垂直位置偏移；double.NaN 表示垂直居中。</summary>
    public double OffsetY { get; set; } = double.NaN;

    /// <summary>是否钉住（不自动收起）。</summary>
    public bool Pinned { get; set; }

    /// <summary>移开鼠标后自动收起的延时（毫秒）。</summary>
    public int CollapseDelayMs { get; set; } = 1500;

    /// <summary>是否启用鼠标移入滑出、移出收起。</summary>
    public bool AutoCollapse { get; set; } = true;

    /// <summary>展开后无操作多少秒自动收起。太短会在用户阅读/思考时误收，最低 6 秒。</summary>
    public int IdleCollapseSeconds { get; set; } = 12;

    /// <summary>把历史配置里过短的自动收起时间抬到可用的下限。</summary>
    public void Normalize()
    {
        // 4 秒是旧版本的默认值，用户当时并没有主动选过；低于它的都按新默认 12 秒处理，
        // 避免"还在看/还在点就被收走"。用户主动设过的 6 秒以上一概尊重。
        IdleCollapseSeconds = IdleCollapseSeconds <= 4
            ? 12
            : Math.Clamp(IdleCollapseSeconds, 6, 60);

        // 650ms 是旧版本的默认值，鼠标一移开就"啪"地收起太过神经质；低于它的都按
        // 新默认 1500ms 处理，用户主动设过的更大值一概尊重。
        CollapseDelayMs = CollapseDelayMs <= 650
            ? 1500
            : Math.Clamp(CollapseDelayMs, 400, 4000);
        PanelWidth = Math.Clamp(PanelWidth, 240, 640);

        // 历史版本的默认文案尾部带一句"eIsland 有的这里全都有"的对比广告，
        // 这不是用户自己写的，跟着版本升级一起清掉，只删这一段，其余文字原样保留。
        const string adTail = "eIsland 有的这里全都有";
        var adIndex = MarqueeText.IndexOf(adTail, StringComparison.OrdinalIgnoreCase);
        if (adIndex >= 0)
        {
            MarqueeText = MarqueeText[..adIndex].Trim().TrimEnd('·', ' ', '　').Trim();
        }

        // 历史版本的默认地点是"平武"，属于开发时随手填的坐标；不再作为默认值，
        // 只把"从没改过定位"（地点与坐标都还是旧默认）的配置换成新默认。
        if (WeatherPlace == "平武" &&
            Math.Abs(WeatherLat - 32.4096) < 0.0001 &&
            Math.Abs(WeatherLon - 104.5276) < 0.0001)
        {
            WeatherLat = DefaultWeatherLat;
            WeatherLon = DefaultWeatherLon;
            WeatherPlace = DefaultWeatherPlace;
        }

        // 没配置本地歌词目录时，在线歌词是唯一的歌词来源。历史版本该值默认 false，
        // 导致"抓不到歌词"；这里自愈为开启，保证有网就能出词。
        if (string.IsNullOrWhiteSpace(LyricsFolder))
        {
            OnlineLyrics = true;
        }
    }

    /// <summary>是否跟随鼠标所在显示器贴边（多显示器）。</summary>
    public bool FollowActiveMonitor { get; set; }

    // —— 外观 ——
    public PanelMaterialKind Material { get; set; } = PanelMaterialKind.LiquidGlass;

    /// <summary>深色 / 浅色 / 跟随系统。</summary>
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    /// <summary>面板底色不透明度 0.3~1.0。</summary>
    public double Opacity { get; set; } = 0.78;

    /// <summary>面板圆角。</summary>
    public double CornerRadius { get; set; } = 18;

    /// <summary>强调色（HEX，如 #FF3B7DD8）。</summary>
    public string AccentColor { get; set; } = "#FF4C8DF6";

    // —— 跑马灯 ——
    public string MarqueeText { get; set; } =
        "欢迎使用 EdgePanel 侧屏  ·  歌词同步 / 跑马灯 / 媒体控制 / 系统监控 / 剪贴板 / 天气 / 待办 / 快捷启动";

    public double MarqueeSpeed { get; set; } = 70;   // 像素/秒
    public double MarqueeFontSize { get; set; } = 15;
    public bool MarqueeEnabled { get; set; } = true;

    // —— 天气（Open-Meteo，免密钥）——
    // 默认给一个通用的大城市坐标（北京），不绑定任何个人所在地。
    private const double DefaultWeatherLat = 39.9042;
    private const double DefaultWeatherLon = 116.4074;
    private const string DefaultWeatherPlace = "北京";

    public double WeatherLat { get; set; } = DefaultWeatherLat;
    public double WeatherLon { get; set; } = DefaultWeatherLon;
    public string WeatherPlace { get; set; } = DefaultWeatherPlace;

    // —— 歌词 ——
    /// <summary>LRC 歌词搜索目录（可多个，用 ; 分隔）。</summary>
    public string LyricsFolder { get; set; } = "";

    /// <summary>本地找不到 .lrc 时是否联网抓歌词（网易云 → LRCLIB）。默认开启。</summary>
    public bool OnlineLyrics { get; set; } = true;

    // —— 其它 ——
    public List<string> Launchers { get; set; } = new();
    public List<string> Todos { get; set; } = new();
    public string Notes { get; set; } = "";
    public bool StartWithWindows { get; set; }

    /// <summary>界面语言：zh-CN / en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>检查更新用的版本信息地址（留空则只显示本地版本）。</summary>
    public string UpdateCheckUrl { get; set; } = "";

    // —— 电脑助手（内置 AI 助手，OpenAI 兼容接口，模型由用户自行配置）——

    /// <summary>OpenAI 兼容 Base URL，例如 https://api.deepseek.com/v1、http://localhost:11434/v1。</summary>
    public string AssistantBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>API Key（部分本地模型可留空）。</summary>
    public string AssistantApiKey { get; set; } = "";

    /// <summary>模型名，例如 deepseek-chat、qwen-plus、gpt-4o-mini。</summary>
    public string AssistantModel { get; set; } = "gpt-4o-mini";

    /// <summary>系统提示词。</summary>
    public string AssistantSystemPrompt { get; set; } =
        "你是 EdgePanel 内置的电脑控制助手，运行在用户的 Windows 电脑上。" +
        "你可以调用工具直接操作这台电脑（鼠标、键盘、截屏、启动/切换窗口、执行命令、读写文件、剪贴板等）。" +
        "需要了解屏幕时先调用 screenshot 截屏。回答用中文，简洁直接，执行完动作后说明结果。";

    /// <summary>允许执行命令行（风险较高）。</summary>
    public bool AssistantAllowCommand { get; set; } = true;

    /// <summary>允许读写本地文件（风险较高）。</summary>
    public bool AssistantAllowFile { get; set; } = true;

    /// <summary>截屏后把画面发给模型（需要模型支持视觉；纯文本模型请保持关闭，否则接口会报错）。</summary>
    public bool AssistantVision { get; set; } = false;

    /// <summary>单轮最多执行多少步工具调用。</summary>
    public int AssistantMaxSteps { get; set; } = 20;

    /// <summary>回放给模型的历史消息条数上限。</summary>
    public int AssistantHistoryLimit { get; set; } = 40;
}

/// <summary>配置读写：%LOCALAPPDATA%\EdgePanel\settings.json</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // OffsetY 默认 double.NaN 表示垂直居中，不放开 NaN 会让整个 Save 抛异常、配置永远写不进去
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string FilePath { get; } = Path.Combine(App.DataRoot, "settings.json");

    private static AppSettings? _current;

    /// <summary>当前配置单例。</summary>
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return _current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("读取配置失败：" + ex.Message);
        }

        return _current = new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataRoot);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            App.Log("保存配置失败：" + ex.Message);
        }
    }
}