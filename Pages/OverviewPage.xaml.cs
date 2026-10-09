using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class OverviewPage : Page
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _hooked;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            _clock.Tick += OnClockTick;
            SystemMonitorService.Instance.Updated += OnSystem;
            WeatherService.Instance.Updated += OnWeather;
        }

        _clock.Start();
        OnClockTick(null, null);
        OnSystem(SystemMonitorService.Instance.Current);

        if (WeatherService.Instance.Current is { } weather)
        {
            OnWeather(weather);
        }
        else
        {
            _ = RefreshWeatherAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clock.Stop();
        SystemMonitorService.Instance.Updated -= OnSystem;
        WeatherService.Instance.Updated -= OnWeather;
        _hooked = false;
    }

    private void OnClockTick(object? sender, object? e)
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString("yyyy年M月d日 dddd");
    }

    private void OnSystem(SystemSnapshot snap)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            CpuText.Text = $"{snap.CpuPercent:0}%";
            CpuBar.Value = snap.CpuPercent;
            MemText.Text = $"{snap.MemoryUsedGb:0.0}G";
            MemBar.Value = snap.MemoryPercent;
            NetText.Text = $"{snap.NetDownKbps:0}";
        });
    }

    private void OnWeather(WeatherNow? weather)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (weather is null)
            {
                WeatherDesc.Text = "天气获取失败，点刷新重试";
                return;
            }

            WeatherGlyph.Text = weather.Emoji;
            WeatherTemp.Text = $"{weather.Temp:0}°C";
            WeatherDesc.Text = $"{weather.Place} · {weather.Describe} · 体感 {weather.FeelsLike:0}° · 湿度 {weather.Humidity:0}%";
        });
    }

    private async Task RefreshWeatherAsync()
    {
        var s = SettingsStore.Current;
        await WeatherService.Instance.RefreshAsync(s.WeatherLat, s.WeatherLon, s.WeatherPlace);
    }

    private async void RefreshWeather_Click(object sender, RoutedEventArgs e) => await RefreshWeatherAsync();

    private void Lock_Click(object sender, RoutedEventArgs e) => SystemActions.LockScreen();

    private void Mute_Click(object sender, RoutedEventArgs e) => SystemActions.ToggleMute();

    private void TaskMgr_Click(object sender, RoutedEventArgs e) => SystemActions.OpenTaskManager();

    private void Recycle_Click(object sender, RoutedEventArgs e) => SystemActions.ClearRecycleBin();

    private void ResMon_Click(object sender, RoutedEventArgs e) => SystemActions.OpenResourceMonitor();

    private void Control_Click(object sender, RoutedEventArgs e) => SystemActions.OpenControlPanel();
}