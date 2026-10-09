using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace EdgePanel.Pages;

public sealed partial class SettingsPage : Page
{
    // 初始为 true：构造阶段设置滑块范围会触发 ValueChanged，
    // 必须先屏蔽这些回调，等 Reload 填完真实值后再放行。
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        // 滑块范围必须在代码里按 Maximum → Minimum 的顺序设置：
        // XAML 编译后属性赋值顺序不保证，若先赋 Minimum 会因超过默认 Maximum(100) 抛异常。
        ConfigureRanges();

        Loaded += (_, _) => Reload();
    }

    private void ConfigureRanges()
    {
        SetRange(WidthSlider, 240, 640, 4);
        SetRange(DelaySlider, 200, 2000, 50);
        SetRange(IdleSlider, 6, 60, 1);
        SetRange(OpacitySlider, 0.3, 1, 0.02);
        SetRange(CornerSlider, 0, 32, 2);
        SetRange(SpeedSlider, 20, 200, 5);
    }

    private static void SetRange(Slider slider, double min, double max, double step)
    {
        slider.Maximum = max;
        slider.Minimum = min;
        slider.StepFrequency = step;
    }

    private void Reload()
    {
        _loading = true;
        var s = SettingsStore.Current;

        PanelToggle.IsOn = s.PanelEnabled;
        FollowMonitorToggle.IsOn = s.FollowActiveMonitor;
        AutoCollapseToggle.IsOn = s.AutoCollapse;

        WidthSlider.Value = Math.Clamp(s.PanelWidth, WidthSlider.Minimum, WidthSlider.Maximum);
        DelaySlider.Value = Math.Clamp(s.CollapseDelayMs, DelaySlider.Minimum, DelaySlider.Maximum);
        IdleSlider.Value = Math.Clamp(s.IdleCollapseSeconds, IdleSlider.Minimum, IdleSlider.Maximum);

        MaterialBox.SelectedIndex = s.Material switch
        {
            PanelMaterialKind.LiquidGlass => 0,
            PanelMaterialKind.Acrylic => 1,
            PanelMaterialKind.Mica => 2,
            _ => 3,
        };

        ThemeBox.SelectedIndex = s.ThemeMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0,
        };

        OpacitySlider.Value = Math.Clamp(s.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        CornerSlider.Value = Math.Clamp(s.CornerRadius, CornerSlider.Minimum, CornerSlider.Maximum);

        MarqueeToggle.IsOn = s.MarqueeEnabled;
        MarqueeBox.Text = s.MarqueeText;
        SpeedSlider.Value = Math.Clamp(s.MarqueeSpeed, SpeedSlider.Minimum, SpeedSlider.Maximum);

        LyricsFolderBox.Text = s.LyricsFolder;
        OnlineLyricsToggle.IsOn = s.OnlineLyrics;

        LanguageBox.SelectedIndex = s.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        AssistantBaseBox.Text = s.AssistantBaseUrl;
        AssistantKeyBox.Password = s.AssistantApiKey;
        AssistantModelBox.Text = s.AssistantModel;
        AssistantVisionToggle.IsOn = s.AssistantVision;
        AssistantCmdToggle.IsOn = s.AssistantAllowCommand;
        AssistantFileToggle.IsOn = s.AssistantAllowFile;

        // 以注册表实际状态为准，避免配置与系统不一致
        StartupToggle.IsOn = StartupService.IsEnabled();

        PathText.Text = $"配置文件：{SettingsStore.FilePath}\n日志目录：{App.LogDir}";
        UpdateText.Text = $"当前版本：{UpdaterService.CurrentVersion}";

        UpdateLabels();
        _loading = false;
    }

    private void UpdateLabels()
    {
        WidthLabel.Text = $"展开宽度：{(int)WidthSlider.Value} 像素";
        DelayLabel.Text = $"收起延时：{(int)DelaySlider.Value} 毫秒";
        IdleLabel.Text = $"无操作自动收起：{(int)IdleSlider.Value} 秒";
        OpacityLabel.Text = $"不透明度：{OpacitySlider.Value * 100:0}%";
        CornerLabel.Text = $"圆角：{(int)CornerSlider.Value} 像素";
        SpeedLabel.Text = $"滚动速度：{(int)SpeedSlider.Value} 像素/秒";
    }

    /// <summary>保存配置并立即刷新面板。</summary>
    private void Apply()
    {
        SettingsStore.Save();
        App.Panel?.ApplySettings();
    }

    // ————————————————————————— 面板 —————————————————————————

    private void PanelToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.PanelEnabled = PanelToggle.IsOn;
        SettingsStore.Save();
        App.SetPanelEnabled(PanelToggle.IsOn);
    }

    private void FollowMonitor_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.FollowActiveMonitor = FollowMonitorToggle.IsOn;
        Apply();
    }

    private void AutoCollapse_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.AutoCollapse = AutoCollapseToggle.IsOn;
        Apply();
    }

    private void Width_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.PanelWidth = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    private void Delay_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.CollapseDelayMs = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    private void Idle_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.IdleCollapseSeconds = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    // ————————————————————————— 外观 —————————————————————————

    private void Material_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (MaterialBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<PanelMaterialKind>(tag, out var kind))
        {
            SettingsStore.Current.Material = kind;
            Apply();
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<AppThemeMode>(tag, out var mode))
        {
            SettingsStore.Current.ThemeMode = mode;
            Apply();
        }
    }

    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.Opacity = Math.Round(e.NewValue, 2);
        UpdateLabels();
        Apply();
    }

    private void Corner_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.CornerRadius = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    // ————————————————————————— 跑马灯 —————————————————————————

    private void Marquee_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.MarqueeEnabled = MarqueeToggle.IsOn;
        Apply();
    }

    private void MarqueeText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.MarqueeText = MarqueeBox.Text ?? "";
        Apply();
    }

    private void Speed_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.MarqueeSpeed = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    // ————————————————————————— 歌词 —————————————————————————

    private async void PickLyricsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");

            var window = App.Panel;
            if (window is null)
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var s = SettingsStore.Current;
            s.LyricsFolder = string.IsNullOrWhiteSpace(s.LyricsFolder)
                ? folder.Path
                : s.LyricsFolder + ";" + folder.Path;
            SettingsStore.Save();

            _loading = true;
            LyricsFolderBox.Text = s.LyricsFolder;
            _loading = false;
        }
        catch (Exception ex)
        {
            App.Log("选择歌词目录失败：" + ex.Message);
        }
    }

    private void ClearLyricsFolder_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.LyricsFolder = "";
        SettingsStore.Save();

        _loading = true;
        LyricsFolderBox.Text = "";
        _loading = false;
    }

    private void OnlineLyrics_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.OnlineLyrics = OnlineLyricsToggle.IsOn;
        SettingsStore.Save();
    }

    // ————————————————————————— 电脑助手 —————————————————————————

    private void AssistantBase_LostFocus(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AssistantBaseUrl = (AssistantBaseBox.Text ?? "").Trim();
        SettingsStore.Save();
    }

    private void AssistantKey_LostFocus(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AssistantApiKey = AssistantKeyBox.Password ?? "";
        SettingsStore.Save();
    }

    private void AssistantModel_LostFocus(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AssistantModel = (AssistantModelBox.Text ?? "").Trim();
        SettingsStore.Save();
    }

    private void AssistantVision_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.AssistantVision = AssistantVisionToggle.IsOn;
        SettingsStore.Save();
    }

    private void AssistantCmd_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.AssistantAllowCommand = AssistantCmdToggle.IsOn;
        SettingsStore.Save();
    }

    private void AssistantFile_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.AssistantAllowFile = AssistantFileToggle.IsOn;
        SettingsStore.Save();
    }

    // ————————————————————————— 其它 —————————————————————————

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            SettingsStore.Current.Language = tag;
            SettingsStore.Save();
            App.Panel?.ApplyLanguage();
        }
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var want = StartupToggle.IsOn;
        var ok = StartupService.SetEnabled(want);

        // 注册表写入失败则回滚开关状态，避免出现"看起来开了其实没开"
        if (!ok)
        {
            _loading = true;
            StartupToggle.IsOn = StartupService.IsEnabled();
            _loading = false;
            return;
        }

        SettingsStore.Current.StartWithWindows = want;
        SettingsStore.Save();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateText.Text = "正在检查更新…";
        var result = await UpdaterService.CheckAsync(SettingsStore.Current.UpdateCheckUrl);
        UpdateText.Text = result;
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => SystemActions.OpenPath(App.DataRoot);

    private void OpenLog_Click(object sender, RoutedEventArgs e) => SystemActions.OpenPath(App.LogDir);

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.Log("用户从设置页退出应用");
        Application.Current.Exit();
    }
}