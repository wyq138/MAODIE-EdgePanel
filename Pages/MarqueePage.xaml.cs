using EdgePanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class MarqueePage : Page
{
    private bool _loading = true;

    public MarqueePage()
    {
        InitializeComponent();

        var s = SettingsStore.Current;
        TextInput.Text = s.MarqueeText;
        SpeedSlider.Value = s.MarqueeSpeed;
        FontSlider.Value = s.MarqueeFontSize;
        EnableSwitch.IsOn = s.MarqueeEnabled;

        Preview.Text = s.MarqueeText;
        Preview.Speed = s.MarqueeSpeed;
        Preview.FontSize = s.MarqueeFontSize;

        UpdateLabels();
        _loading = false;
    }

    private void UpdateLabels()
    {
        SpeedText.Text = $"{SpeedSlider.Value:0} px/s";
        FontText.Text = $"{FontSlider.Value:0}";
    }

    private void TextInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Preview.Text = TextInput.Text;
    }

    private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Preview.Speed = e.NewValue;
        UpdateLabels();
    }

    private void FontSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Preview.FontSize = e.NewValue;
        UpdateLabels();
    }

    private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsStore.Current.MarqueeEnabled = EnableSwitch.IsOn;
        SettingsStore.Save();
        ApplyToPanels();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current;
        s.MarqueeText = TextInput.Text;
        s.MarqueeSpeed = SpeedSlider.Value;
        s.MarqueeFontSize = FontSlider.Value;
        s.MarqueeEnabled = EnableSwitch.IsOn;
        SettingsStore.Save();
        ApplyToPanels();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        TextInput.Text = $"EdgePanel · {DateTime.Now:HH:mm:ss} · 音乐控制 / 同步歌词 / 跑马灯 / 系统监测 / 剪贴板历史 / 8 天天气 / 待办便签 / 快捷启动，一个侧屏全搞定";
        Preview.Text = TextInput.Text;
    }

    private static void ApplyToPanels()
    {
        App.Panel?.ApplySettings();
    }
}